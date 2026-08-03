#:package NuGet.Protocol@7.3.1
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

internal static class App
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = CliArgs.Parse(args);
            if (parsed is null)
            {
                PrintUsage();
                return 1;
            }

            var result = await Inspector.InspectAsync(parsed);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

            if (string.IsNullOrEmpty(parsed.OutputPath))
            {
                Console.Out.Write(json);
            }
            else
            {
                await File.WriteAllTextAsync(parsed.OutputPath, json);
                Console.Error.WriteLine($"Wrote: {parsed.OutputPath}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(ex.ToString());
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: inspect-nuget-package inspect <packageId> [--version <ver>] [--tfm <tfm>] " +
            "[--source <feed-url>] [--output <file>] [--summary] [--filter <regex>] [--include-internal] " +
            "[--max-members-per-type N] [--no-source-link]\n" +
            "Output: JSON describing the package's public API. Defaults to stdout (recommended; parse it in-process).\n" +
            "All package processing is done in memory; no temp files are written.\n" +
            "Note: --output is an escape hatch for human users. Automated agents should read from stdout, not write files.");
    }
}

internal sealed record CliArgs(
    string Command,
    string PackageId,
    string? Version,
    string? Tfm,
    string Source,
    string? OutputPath,
    bool IncludeInternal,
    int MaxMembersPerType,
    bool NoSourceLink,
    bool SummaryOnly,
    Regex? Filter)
{
    public static CliArgs? Parse(string[] args)
    {
        if (args.Length < 2) return null;
        var cmd = args[0];
        if (!string.Equals(cmd, "inspect", StringComparison.OrdinalIgnoreCase)) return null;
        var packageId = args[1];
        string? version = null;
        string? tfm = null;
        string source = "https://api.nuget.org/v3/index.json";
        string? output = null;
        bool includeInternal = false;
        int maxMembers = 0;
        bool noSourceLink = false;
        bool summaryOnly = false;
        Regex? filter = null;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version": version = args[++i]; break;
                case "--tfm": tfm = args[++i]; break;
                case "--source": source = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--include-internal": includeInternal = true; break;
                case "--max-members-per-type": maxMembers = int.Parse(args[++i]); break;
                case "--no-source-link": noSourceLink = true; break;
                case "--summary": summaryOnly = true; break;
                case "--filter": filter = new Regex(args[++i], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); break;
                default:
                    Console.Error.WriteLine("Unknown arg: " + args[i]);
                    return null;
            }
        }

        return new CliArgs(cmd, packageId, version, tfm, source, output,
            includeInternal, maxMembers, noSourceLink, summaryOnly, filter);
    }
}

internal sealed record InMemoryAssembly(string Key, byte[] Dll, byte[]? Xml);

internal static class Inspector
{
    public static async Task<object> InspectAsync(CliArgs args)
    {
        var logger = NullLogger.Instance;
        var ct = CancellationToken.None;

        var sourceRepo = Repository.Factory.GetCoreV3(args.Source);
        var findResource = await sourceRepo.GetResourceAsync<FindPackageByIdResource>(ct);

        // NoCache + DirectDownload prevents NuGet from writing to its HTTP cache directory.
        var sourceCacheContext = new SourceCacheContext { NoCache = true, DirectDownload = true };

        // Resolve version
        var allVersions = (await findResource.GetAllVersionsAsync(args.PackageId, sourceCacheContext, logger, ct)).ToList();
        if (allVersions.Count == 0) throw new Exception($"Package not found: {args.PackageId}");

        NuGetVersion version = ResolveVersion(args.Version, allVersions);

        // Download nupkg into memory
        var nupkgBytes = await DownloadNupkgAsync(findResource, args.PackageId, version, sourceCacheContext, logger, ct);

        using var packageReader = new PackageArchiveReader(new MemoryStream(nupkgBytes, writable: false));
        var nuspec = packageReader.NuspecReader;

        // Pick TFM and assembly group: prefer ref/, fallback lib/
        var refGroups = (await packageReader.GetReferenceItemsAsync(ct)).ToList();
        var libGroups = (await packageReader.GetLibItemsAsync(ct)).ToList();
        var groupSource = refGroups.Count > 0 ? refGroups : libGroups;

        if (groupSource.Count == 0)
        {
            return new
            {
                package = new { id = args.PackageId, version = version.ToString() },
                error = "No managed reference or lib assemblies found in package. Likely a content/build/analyzer-only package.",
                title = nuspec.GetTitle(),
                description = nuspec.GetDescription(),
            };
        }

        FrameworkSpecificGroup chosenGroup;
        NuGetFramework? requestedFramework = null;
        bool tfmSubstituted = false;
        if (!string.IsNullOrEmpty(args.Tfm))
        {
            requestedFramework = NuGetFramework.Parse(args.Tfm);
            var exact = groupSource.FirstOrDefault(g => g.TargetFramework.Equals(requestedFramework));
            if (exact is not null)
            {
                chosenGroup = exact;
            }
            else
            {
                var nearest = NuGetFrameworkUtility.GetNearest(groupSource, requestedFramework, g => g.TargetFramework);
                chosenGroup = nearest ?? PickBestFramework(groupSource);
                tfmSubstituted = !chosenGroup.TargetFramework.Equals(requestedFramework);
            }
        }
        else
        {
            chosenGroup = PickBestFramework(groupSource);
        }

        // Load primary assemblies into memory (DLL + matching XML)
        var primaryAssemblies = new List<InMemoryAssembly>();
        var apiSource = "lib/";
        foreach (var entry in chosenGroup.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            if (entry.StartsWith("ref/", StringComparison.OrdinalIgnoreCase)) apiSource = "ref/";
            var dll = await ReadEntryBytesAsync(packageReader, entry, ct);
            if (dll is null) continue;
            var xmlEntry = Path.ChangeExtension(entry, ".xml").Replace('\\', '/');
            var xml = await ReadEntryBytesAsync(packageReader, xmlEntry, ct);
            primaryAssemblies.Add(new InMemoryAssembly(Path.GetFileName(entry), dll, xml));
        }

        if (primaryAssemblies.Count == 0)
        {
            return new
            {
                package = new { id = args.PackageId, version = version.ToString() },
                error = $"No DLLs found for chosen TFM '{chosenGroup.TargetFramework}'.",
            };
        }

        // Inspect each assembly straight from its ECMA-335 metadata tables. Signatures are
        // decoded to names without resolving referenced assemblies, so unresolvable NuGet
        // dependencies and framework references (Microsoft.AspNetCore.App, ...) cannot hide
        // members from the output.
        var apiAssemblies = new List<object>();
        foreach (var primary in primaryAssemblies)
        {
            var xmlDocs = XmlDocs.Load(primary.Xml);
            apiAssemblies.Add(ExtractAssembly(primary, xmlDocs, args));
        }

        // Dependencies summary
        var depGroups = nuspec.GetDependencyGroups().ToList();
        var depBest = NuGetFrameworkUtility.GetNearest(depGroups, chosenGroup.TargetFramework, g => g.TargetFramework);
        var dependencies = (depBest?.Packages ?? Enumerable.Empty<PackageDependency>())
            .Select(p => new { id = p.Id, range = p.VersionRange.ToShortString() })
            .ToList();

        var frameworkRefGroups = nuspec.GetFrameworkRefGroups().ToList();
        var frameworkRefBest = NuGetFrameworkUtility.GetNearest(frameworkRefGroups, chosenGroup.TargetFramework, g => g.TargetFramework);
        var frameworkReferences = (frameworkRefBest?.FrameworkReferences ?? Enumerable.Empty<FrameworkReference>())
            .Select(f => f.Name)
            .ToList();

        return new
        {
            package = new
            {
                id = args.PackageId,
                version = version.ToString(),
                title = nuspec.GetTitle(),
                description = nuspec.GetDescription(),
                authors = nuspec.GetAuthors(),
                projectUrl = nuspec.GetProjectUrl(),
                repositoryUrl = nuspec.GetRepositoryMetadata()?.Url,
                repositoryCommit = nuspec.GetRepositoryMetadata()?.Commit,
                license = nuspec.GetLicenseMetadata()?.License ?? nuspec.GetLicenseUrl(),
                tags = nuspec.GetTags(),
            },
            chosenTargetFramework = chosenGroup.TargetFramework.GetShortFolderName(),
            requestedTargetFramework = requestedFramework?.GetShortFolderName(),
            targetFrameworkSubstituted = tfmSubstituted,
            availableTargetFrameworks = groupSource.Select(g => g.TargetFramework.GetShortFolderName()).ToList(),
            dependencies,
            frameworkReferences,
            assemblies = apiAssemblies,
            notes = new
            {
                apiSource,
                extractionEngine = "System.Reflection.Metadata (MetadataReader)",
                filter = args.Filter?.ToString(),
                hint = "Public API decoded directly from ECMA-335 metadata. Referenced assemblies are never " +
                       "resolved, so unresolvable dependencies cannot hide members. All work is in memory.",
            }
        };
    }

    private static object ExtractAssembly(InMemoryAssembly primary, XmlDocs docs, CliArgs args)
    {
        try
        {
            using var peStream = new MemoryStream(primary.Dll, writable: false);
            using var peReader = new PEReader(peStream);
            if (!peReader.HasMetadata)
                return new { file = primary.Key, error = "Not a managed assembly (no CLI metadata)." };

            var reader = peReader.GetMetadataReader();
            var sourceLink = args.NoSourceLink ? null : SourceLinkReader.TryRead(peReader);
            return new ApiExtractor(reader, docs, args).Extract(primary.Key, sourceLink);
        }
        catch (Exception ex)
        {
            return new { file = primary.Key, error = ex.Message };
        }
    }

    private static async Task<byte[]> DownloadNupkgAsync(
        FindPackageByIdResource findResource,
        string id,
        NuGetVersion version,
        SourceCacheContext cache,
        ILogger logger,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var ok = await findResource.CopyNupkgToStreamAsync(id, version, ms, cache, logger, ct);
        if (!ok) throw new Exception($"Failed to download {id} {version}");
        return ms.ToArray();
    }

    private static async Task<byte[]?> ReadEntryBytesAsync(PackageArchiveReader pkg, string entry, CancellationToken ct)
    {
        try
        {
            using var src = pkg.GetStream(entry);
            using var ms = new MemoryStream();
            await src.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static NuGetVersion ResolveVersion(string? requested, List<NuGetVersion> all)
    {
        // No version requested: latest stable, fallback to latest prerelease
        if (string.IsNullOrEmpty(requested))
        {
            var stable = all.Where(v => !v.IsPrerelease).ToList();
            return (stable.Count > 0 ? stable : all).Max()!;
        }

        // 1) Exact parse + exact match
        if (NuGetVersion.TryParse(requested, out var exact))
        {
            var hit = all.FirstOrDefault(v => v.Equals(exact));
            if (hit is not null) return hit;
        }

        // 2) Floating: treat as a prefix (e.g. "4", "4.0", "4.0.0")
        var prefix = requested.TrimEnd('.', '*');
        var candidates = all
            .Where(v => v.ToNormalizedString().StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                     || v.ToNormalizedString().Equals(prefix, StringComparison.OrdinalIgnoreCase)
                     || v.ToNormalizedString().StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count > 0)
        {
            var stableMatch = candidates.Where(v => !v.IsPrerelease).ToList();
            return (stableMatch.Count > 0 ? stableMatch : candidates).Max()!;
        }

        // 3) NuGet VersionRange (e.g. "[4.0,5.0)")
        if (VersionRange.TryParse(requested, out var range))
        {
            var match = range.FindBestMatch(all);
            if (match is not null) return match;
        }

        var sample = string.Join(", ", all.OrderByDescending(v => v).Take(5).Select(v => v.ToNormalizedString()));
        throw new Exception(
            $"No version matches '{requested}'. Available (top 5): {sample}. " +
            $"Query https://api.nuget.org/v3-flatcontainer/<id-lowercase>/index.json for the full list.");
    }

    private static FrameworkSpecificGroup PickBestFramework(List<FrameworkSpecificGroup> groups)
    {
        // Prefer .NET (Core/5+) highest, then netstandard2.1, then netstandard2.0, then anything
        var net = groups
            .Where(g => g.TargetFramework.Framework == ".NETCoreApp")
            .OrderByDescending(g => g.TargetFramework.Version)
            .FirstOrDefault();
        if (net is not null) return net;

        var ns21 = groups.FirstOrDefault(g => g.TargetFramework.GetShortFolderName() == "netstandard2.1");
        if (ns21 is not null) return ns21;
        var ns20 = groups.FirstOrDefault(g => g.TargetFramework.GetShortFolderName() == "netstandard2.0");
        if (ns20 is not null) return ns20;

        return groups.OrderByDescending(g => g.TargetFramework.Version).First();
    }
}

/// <summary>
/// Generic parameter names in scope while a signature blob is decoded.
/// Metadata indexes type parameters globally across the nesting chain, which is exactly
/// how <see cref="TypeDefinition.GetGenericParameters"/> orders them.
/// </summary>
internal readonly struct GenericContext(ImmutableArray<string> typeParameters, ImmutableArray<string> methodParameters)
{
    public ImmutableArray<string> TypeParameters { get; } = typeParameters;
    public ImmutableArray<string> MethodParameters { get; } = methodParameters;

    public static readonly GenericContext Empty =
        new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

    public GenericContext WithMethod(ImmutableArray<string> methodParameters) =>
        new(TypeParameters, methodParameters);
}

internal static class Names
{
    private static readonly Regex ArityRegex = new(@"`\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string StripArity(string name) => ArityRegex.Replace(name, "");

    public static string OfTypeDef(MetadataReader r, TypeDefinitionHandle handle, char nestSeparator)
    {
        var td = r.GetTypeDefinition(handle);
        var name = r.GetString(td.Name);
        if (td.IsNested)
            return OfTypeDef(r, td.GetDeclaringType(), nestSeparator) + nestSeparator + name;
        var ns = r.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string OfTypeRef(MetadataReader r, TypeReferenceHandle handle, char nestSeparator)
    {
        var tr = r.GetTypeReference(handle);
        var name = r.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return OfTypeRef(r, (TypeReferenceHandle)tr.ResolutionScope, nestSeparator) + nestSeparator + name;
        var ns = r.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Namespace of the outermost enclosing type (nested TypeDefs carry an empty namespace).</summary>
    public static string NamespaceOf(MetadataReader r, TypeDefinitionHandle handle)
    {
        var td = r.GetTypeDefinition(handle);
        while (td.IsNested)
            td = r.GetTypeDefinition(td.GetDeclaringType());
        return r.GetString(td.Namespace);
    }
}

/// <summary>
/// Renders signature blobs as strings. Two flavours: display (<c>Foo&lt;T&gt;</c>, <c>+</c> for nested,
/// <c>&amp;</c> for byref) and XML documentation-comment IDs (<c>Foo{T}</c>, <c>.</c> for nested, <c>@</c> for byref).
/// Neither ever needs the referenced assembly to be loadable.
/// </summary>
internal sealed class TypeNameProvider(bool docIdFormat)
    : ISignatureTypeProvider<string, GenericContext>
{
    private char NestSeparator => docIdFormat ? '.' : '+';

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "System.Void",
        _ => "System.Object",
    };

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
        => Names.OfTypeDef(r, handle, NestSeparator);

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
        => Names.OfTypeRef(r, handle, NestSeparator);

    public string GetTypeFromSpecification(MetadataReader r, GenericContext ctx, TypeSpecificationHandle handle, byte rawTypeKind)
        => r.GetTypeSpecification(handle).DecodeSignature(this, ctx);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => docIdFormat
        ? elementType + "[" + string.Join(",", Enumerable.Repeat("0:", shape.Rank)) + "]"
        : elementType + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]";

    public string GetByReferenceType(string elementType) => elementType + (docIdFormat ? "@" : "&");

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => Names.StripArity(genericType) + (docIdFormat
            ? "{" + string.Join(",", typeArguments) + "}"
            : "<" + string.Join(", ", typeArguments) + ">");

    public string GetGenericTypeParameter(GenericContext ctx, int index) => docIdFormat
        ? "`" + index.ToString(CultureInfo.InvariantCulture)
        : index < ctx.TypeParameters.Length ? ctx.TypeParameters[index] : "T" + index.ToString(CultureInfo.InvariantCulture);

    public string GetGenericMethodParameter(GenericContext ctx, int index) => docIdFormat
        ? "``" + index.ToString(CultureInfo.InvariantCulture)
        : index < ctx.MethodParameters.Length ? ctx.MethodParameters[index] : "M" + index.ToString(CultureInfo.InvariantCulture);

    public string GetFunctionPointerType(MethodSignature<string> signature)
        => "delegate*<" + string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType)) + ">";

    // Custom modifiers (modreq/modopt) carry calling-convention and `in`/`out` detail that the
    // parameter flags already express; the underlying type is what callers care about.
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetPinnedType(string elementType) => elementType;
}

internal sealed class ApiExtractor(MetadataReader reader, XmlDocs docs, CliArgs args)
{
    private readonly TypeNameProvider _display = new(docIdFormat: false);
    private readonly TypeNameProvider _docId = new(docIdFormat: true);
    private readonly int _cap = args.MaxMembersPerType > 0 ? args.MaxMembersPerType : int.MaxValue;

    public object Extract(string fileName, SourceLinkInfo? sourceLink)
    {
        var visible = new List<TypeDefinitionHandle>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(handle);
            if (reader.GetString(td.Name) == "<Module>") continue;
            if (IsTypeVisible(td)) visible.Add(handle);
        }

        var byNamespace = visible
            .Select(h => (Handle: h, Namespace: Names.NamespaceOf(reader, h), Name: Names.OfTypeDef(reader, h, '+')))
            .GroupBy(x => x.Namespace)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                @namespace = g.Key,
                types = g.OrderBy(x => x.Name, StringComparer.Ordinal)
                         .Select(x => SafeDescribeType(x.Handle))
                         .Where(t => t is not null)
                         .ToList()!,
            })
            .Where(g => g.types.Count > 0)
            .ToList();

        var asmName = reader.IsAssembly ? reader.GetAssemblyDefinition() : default;
        return new
        {
            file = fileName,
            assemblyName = reader.IsAssembly ? reader.GetString(asmName.Name) : null,
            assemblyVersion = reader.IsAssembly ? asmName.Version.ToString() : null,
            sourceLink = sourceLink is null ? null : new
            {
                hasPdb = true,
                documentMappings = sourceLink.UrlMap.Take(8).Select(kv => new { local = kv.Key, url = kv.Value }).ToList(),
            },
            xmlDocs = new
            {
                available = docs.Present,
                documentedMembers = docs.Count,
                recoveredFromMalformedXml = docs.Recovered,
            },
            namespaces = byNamespace,
        };
    }

    // ---- visibility -------------------------------------------------------

    private bool IsTypeVisible(TypeDefinition td)
    {
        var visibility = td.Attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public) return true;
        if (visibility == TypeAttributes.NotPublic) return args.IncludeInternal;

        var declaring = td.GetDeclaringType();
        if (declaring.IsNil) return false;
        if (!IsTypeVisible(reader.GetTypeDefinition(declaring))) return false;

        return visibility switch
        {
            TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => true,
            TypeAttributes.NestedAssembly or TypeAttributes.NestedFamANDAssem => args.IncludeInternal,
            _ => false, // NestedPrivate
        };
    }

    private bool IsVisible(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem => true,
        MethodAttributes.Assembly or MethodAttributes.FamANDAssem => args.IncludeInternal,
        _ => false,
    };

    private bool IsVisible(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem => true,
        FieldAttributes.Assembly or FieldAttributes.FamANDAssem => args.IncludeInternal,
        _ => false,
    };

    private static string Access(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.FamANDAssem => "private protected",
        _ => "private",
    };

    private static string Access(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family => "protected",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.Assembly => "internal",
        FieldAttributes.FamANDAssem => "private protected",
        _ => "private",
    };

    // ---- types ------------------------------------------------------------

    private object? SafeDescribeType(TypeDefinitionHandle handle)
    {
        try { return DescribeType(handle); }
        catch (Exception ex)
        {
            // A type we cannot describe is still reported, never dropped.
            return new { name = Names.OfTypeDef(reader, handle, '+'), error = ex.Message };
        }
    }

    private object? DescribeType(TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        var rawName = Names.OfTypeDef(reader, handle, '+');
        var typeParameters = GenericParameterNames(td.GetGenericParameters());
        var ctx = new GenericContext(typeParameters, ImmutableArray<string>.Empty);

        // A nested type's metadata generic parameter list is prefixed by the enclosing type's.
        var inherited = 0;
        if (td.IsNested)
            inherited = reader.GetTypeDefinition(td.GetDeclaringType()).GetGenericParameters().Count;
        var ownTypeParameters = typeParameters.Skip(inherited).ToList();

        var displayName = ownTypeParameters.Count > 0
            ? Names.StripArity(rawName) + "<" + string.Join(", ", ownTypeParameters) + ">"
            : rawName;
        var typeDocId = "T:" + Names.OfTypeDef(reader, handle, '.');
        var summary = docs.ForMember(typeDocId);

        var accessors = CollectAccessors(td);
        var kind = TypeKind(td, ctx);

        var typeNameMatches = args.Filter is null || TypeNameMatches(rawName);

        if (args.SummaryOnly)
        {
            if (!typeNameMatches && !AnyMemberMatches(td, accessors)) return null;
            return new { name = displayName, fullName = rawName, kind, summary };
        }

        var constructors = new List<object>();
        var methods = new List<object>();
        var properties = new List<object>();
        var fields = new List<object>();
        var events = new List<object>();
        var nestedTypes = new List<object>();

        foreach (var mh in td.GetMethods())
        {
            if (accessors.Contains(mh)) continue;
            var md = reader.GetMethodDefinition(mh);
            if (!IsVisible(md.Attributes)) continue;
            var name = reader.GetString(md.Name);
            if (!typeNameMatches && !args.Filter!.IsMatch(name)) continue;

            var isCtor = name is ".ctor" or ".cctor";
            var target = isCtor ? constructors : methods;
            if (target.Count >= _cap) continue;
            target.Add(SafeDescribeMethod(mh, ctx, rawName, name));
        }

        foreach (var ph in td.GetProperties())
        {
            var pd = reader.GetPropertyDefinition(ph);
            var acc = pd.GetAccessors();
            var visible = (!acc.Getter.IsNil && IsVisible(reader.GetMethodDefinition(acc.Getter).Attributes))
                       || (!acc.Setter.IsNil && IsVisible(reader.GetMethodDefinition(acc.Setter).Attributes));
            if (!visible) continue;
            var name = reader.GetString(pd.Name);
            if (!typeNameMatches && !args.Filter!.IsMatch(name)) continue;
            if (properties.Count >= _cap) continue;
            properties.Add(SafeDescribeProperty(ph, ctx, rawName, name));
        }

        foreach (var fh in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fh);
            if (!IsVisible(fd.Attributes)) continue;
            var name = reader.GetString(fd.Name);
            if (!typeNameMatches && !args.Filter!.IsMatch(name)) continue;
            if (fields.Count >= _cap) continue;
            fields.Add(SafeDescribeField(fh, ctx, rawName, name));
        }

        foreach (var eh in td.GetEvents())
        {
            var ed = reader.GetEventDefinition(eh);
            var adder = ed.GetAccessors().Adder;
            if (adder.IsNil || !IsVisible(reader.GetMethodDefinition(adder).Attributes)) continue;
            var name = reader.GetString(ed.Name);
            if (!typeNameMatches && !args.Filter!.IsMatch(name)) continue;
            if (events.Count >= _cap) continue;
            events.Add(SafeDescribeEvent(eh, ctx, rawName, name));
        }

        foreach (var nh in td.GetNestedTypes())
        {
            var nested = reader.GetTypeDefinition(nh);
            if (!IsTypeVisible(nested)) continue;
            if (nestedTypes.Count >= _cap) continue;
            nestedTypes.Add(new { name = reader.GetString(nested.Name), kind = TypeKind(nested, ctx) });
        }

        if (!typeNameMatches && constructors.Count == 0 && methods.Count == 0 && properties.Count == 0
            && fields.Count == 0 && events.Count == 0)
            return null;

        var baseTypeName = td.BaseType.IsNil ? null : EntityTypeName(td.BaseType, ctx, docId: false);

        return new
        {
            name = displayName,
            fullName = rawName,
            kind,
            isStatic = (td.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed),
            isAbstract = (td.Attributes & TypeAttributes.Abstract) != 0 && (td.Attributes & TypeAttributes.Sealed) == 0 && (td.Attributes & TypeAttributes.Interface) == 0,
            isSealed = (td.Attributes & TypeAttributes.Sealed) != 0 && (td.Attributes & TypeAttributes.Abstract) == 0,
            baseType = baseTypeName == "System.Object" ? null : baseTypeName,
            interfaces = td.GetInterfaceImplementations()
                .Select(ih => EntityTypeName(reader.GetInterfaceImplementation(ih).Interface, ctx, docId: false))
                .ToList(),
            genericParameters = ownTypeParameters.Count > 0 ? ownTypeParameters : null,
            attributes = AttributeNames(td.GetCustomAttributes()),
            summary,
            constructors,
            methods,
            properties,
            fields,
            events,
            nestedTypes,
        };
    }

    /// <summary>
    /// A type matches --filter on either its full name or its simple name, so both
    /// <c>System.Text.Json.JsonTokenType</c> and <c>^JsonTokenType$</c> select the same type.
    /// </summary>
    private bool TypeNameMatches(string rawName)
    {
        if (args.Filter!.IsMatch(rawName)) return true;
        var lastSeparator = rawName.LastIndexOfAny(['.', '+']);
        return lastSeparator >= 0 && args.Filter.IsMatch(rawName[(lastSeparator + 1)..]);
    }

    private bool AnyMemberMatches(TypeDefinition td, HashSet<MethodDefinitionHandle> accessors)
    {
        var filter = args.Filter!;
        foreach (var mh in td.GetMethods())
        {
            if (accessors.Contains(mh)) continue;
            var md = reader.GetMethodDefinition(mh);
            if (IsVisible(md.Attributes) && filter.IsMatch(reader.GetString(md.Name))) return true;
        }
        foreach (var ph in td.GetProperties())
            if (filter.IsMatch(reader.GetString(reader.GetPropertyDefinition(ph).Name))) return true;
        foreach (var fh in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fh);
            if (IsVisible(fd.Attributes) && filter.IsMatch(reader.GetString(fd.Name))) return true;
        }
        foreach (var eh in td.GetEvents())
            if (filter.IsMatch(reader.GetString(reader.GetEventDefinition(eh).Name))) return true;
        return false;
    }

    private HashSet<MethodDefinitionHandle> CollectAccessors(TypeDefinition td)
    {
        var set = new HashSet<MethodDefinitionHandle>();
        foreach (var ph in td.GetProperties())
        {
            var acc = reader.GetPropertyDefinition(ph).GetAccessors();
            if (!acc.Getter.IsNil) set.Add(acc.Getter);
            if (!acc.Setter.IsNil) set.Add(acc.Setter);
            foreach (var other in acc.Others) set.Add(other);
        }
        foreach (var eh in td.GetEvents())
        {
            var acc = reader.GetEventDefinition(eh).GetAccessors();
            if (!acc.Adder.IsNil) set.Add(acc.Adder);
            if (!acc.Remover.IsNil) set.Add(acc.Remover);
            if (!acc.Raiser.IsNil) set.Add(acc.Raiser);
            foreach (var other in acc.Others) set.Add(other);
        }
        return set;
    }

    private string TypeKind(TypeDefinition td, GenericContext ctx)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return "interface";
        var baseName = td.BaseType.IsNil ? null : EntityTypeName(td.BaseType, ctx, docId: false);
        if (baseName == "System.Enum") return "enum";
        if (baseName is "System.MulticastDelegate" or "System.Delegate") return "delegate";
        if (baseName == "System.ValueType") return "struct";
        if ((td.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed))
            return "static class";
        return "class";
    }

    // ---- members ----------------------------------------------------------

    private object SafeDescribeMethod(MethodDefinitionHandle handle, GenericContext typeCtx, string declaringType, string name)
    {
        try { return DescribeMethod(handle, typeCtx, declaringType, name); }
        catch (Exception ex) { return new { name, error = ex.Message }; }
    }

    private object DescribeMethod(MethodDefinitionHandle handle, GenericContext typeCtx, string declaringType, string name)
    {
        var md = reader.GetMethodDefinition(handle);
        var methodParameters = GenericParameterNames(md.GetGenericParameters());
        var ctx = typeCtx.WithMethod(methodParameters);

        var signature = md.DecodeSignature(_display, ctx);
        var docSignature = md.DecodeSignature(_docId, ctx);

        // Parameter rows are optional in metadata; index them by sequence number (0 == return value).
        var rows = new Dictionary<int, Parameter>();
        foreach (var handleOfParam in md.GetParameters())
        {
            var p = reader.GetParameter(handleOfParam);
            rows[p.SequenceNumber] = p;
        }

        var methodContext = NullableContext(md.GetCustomAttributes(), () => TypeNullableContext(md.GetDeclaringType()));

        var parameters = new List<object>();
        for (int i = 0; i < signature.ParameterTypes.Length; i++)
        {
            var type = signature.ParameterTypes[i];
            rows.TryGetValue(i + 1, out var row);
            var attrs = row.Equals(default(Parameter)) ? default : row.Attributes;
            var modifier = type.EndsWith("&", StringComparison.Ordinal)
                ? (attrs & ParameterAttributes.Out) != 0 ? "out" : (attrs & ParameterAttributes.In) != 0 ? "in" : "ref"
                : "";
            var hasDefault = !row.Equals(default(Parameter)) && !row.GetDefaultValue().IsNil;

            parameters.Add(new
            {
                name = row.Equals(default(Parameter)) ? "arg" + i.ToString(CultureInfo.InvariantCulture) : reader.GetString(row.Name),
                type,
                optional = (attrs & ParameterAttributes.Optional) != 0 || hasDefault,
                @default = hasDefault ? ConstantValue(row.GetDefaultValue()) : null,
                modifier,
                nullability = Nullability(type, row.Equals(default(Parameter)) ? null : row.GetCustomAttributes(), methodContext),
            });
        }

        var isCtor = name is ".ctor" or ".cctor";
        var attributeNames = AttributeNames(md.GetCustomAttributes());
        var returnRow = rows.TryGetValue(0, out var rr) ? rr : default;

        return new
        {
            name = isCtor ? ".ctor" : name,
            access = Access(md.Attributes),
            isStatic = (md.Attributes & MethodAttributes.Static) != 0,
            isAbstract = (md.Attributes & MethodAttributes.Abstract) != 0,
            isVirtual = (md.Attributes & MethodAttributes.Virtual) != 0 && (md.Attributes & MethodAttributes.Final) == 0,
            isOverride = (md.Attributes & MethodAttributes.Virtual) != 0 && (md.Attributes & MethodAttributes.NewSlot) == 0,
            isExtension = attributeNames.Contains("System.Runtime.CompilerServices.ExtensionAttribute"),
            isOperator = name.StartsWith("op_", StringComparison.Ordinal),
            returnType = isCtor ? null : signature.ReturnType,
            returnNullability = isCtor ? null
                : Nullability(signature.ReturnType, returnRow.Equals(default(Parameter)) ? null : returnRow.GetCustomAttributes(), methodContext),
            genericParameters = methodParameters.Length > 0 ? methodParameters.ToList() : null,
            parameters,
            attributes = attributeNames,
            summary = docs.ForMember(MethodDocId(declaringType, name, methodParameters.Length, docSignature)),
            signature = FormatSignature(md, name, isCtor, declaringType, signature, methodParameters, parameters),
        };
    }

    private object SafeDescribeProperty(PropertyDefinitionHandle handle, GenericContext ctx, string declaringType, string name)
    {
        try { return DescribeProperty(handle, ctx, declaringType, name); }
        catch (Exception ex) { return new { name, error = ex.Message }; }
    }

    private object DescribeProperty(PropertyDefinitionHandle handle, GenericContext ctx, string declaringType, string name)
    {
        var pd = reader.GetPropertyDefinition(handle);
        var signature = pd.DecodeSignature(_display, ctx);
        var docSignature = pd.DecodeSignature(_docId, ctx);
        var acc = pd.GetAccessors();

        var primaryHandle = acc.Getter.IsNil ? acc.Setter : acc.Getter;
        MethodDefinition? getter = acc.Getter.IsNil ? null : reader.GetMethodDefinition(acc.Getter);
        MethodDefinition? setter = acc.Setter.IsNil ? null : reader.GetMethodDefinition(acc.Setter);
        var primary = reader.GetMethodDefinition(primaryHandle);

        var docIdString = "P:" + Names.StripArity(DocTypeName(declaringType)) + "." + DocMemberName(name)
            + (docSignature.ParameterTypes.Length > 0 ? "(" + string.Join(",", docSignature.ParameterTypes) + ")" : "");

        var context = NullableContext(pd.GetCustomAttributes(), () => TypeNullableContext(primary.GetDeclaringType()));

        return new
        {
            name,
            type = signature.ReturnType,
            canRead = getter is not null,
            canWrite = setter is not null,
            getterAccess = getter is null ? null : Access(getter.Value.Attributes),
            setterAccess = setter is null ? null : Access(setter.Value.Attributes),
            isStatic = (primary.Attributes & MethodAttributes.Static) != 0,
            access = Access(primary.Attributes),
            nullability = Nullability(signature.ReturnType, pd.GetCustomAttributes(), context),
            indexerParameters = signature.ParameterTypes.Length == 0 ? null
                : signature.ParameterTypes.Select((t, i) => new { name = "index" + i.ToString(CultureInfo.InvariantCulture), type = t }).ToList(),
            attributes = AttributeNames(pd.GetCustomAttributes()),
            summary = docs.ForMember(docIdString),
        };
    }

    private object SafeDescribeField(FieldDefinitionHandle handle, GenericContext ctx, string declaringType, string name)
    {
        try { return DescribeField(handle, ctx, declaringType, name); }
        catch (Exception ex) { return new { name, error = ex.Message }; }
    }

    private object DescribeField(FieldDefinitionHandle handle, GenericContext ctx, string declaringType, string name)
    {
        var fd = reader.GetFieldDefinition(handle);
        var type = fd.DecodeSignature(_display, ctx);
        var isConst = (fd.Attributes & FieldAttributes.Literal) != 0;

        return new
        {
            name,
            type,
            isStatic = (fd.Attributes & FieldAttributes.Static) != 0,
            isReadOnly = (fd.Attributes & FieldAttributes.InitOnly) != 0,
            isConst,
            constantValue = isConst ? ConstantValue(fd.GetDefaultValue()) : null,
            access = Access(fd.Attributes),
            nullability = Nullability(type, fd.GetCustomAttributes(), TypeNullableContext(fd.GetDeclaringType())),
            attributes = AttributeNames(fd.GetCustomAttributes()),
            summary = docs.ForMember("F:" + Names.StripArity(DocTypeName(declaringType)) + "." + DocMemberName(name)),
        };
    }

    private object SafeDescribeEvent(EventDefinitionHandle handle, GenericContext ctx, string declaringType, string name)
    {
        try
        {
            var ed = reader.GetEventDefinition(handle);
            return new
            {
                name,
                handlerType = EntityTypeName(ed.Type, ctx, docId: false),
                access = Access(reader.GetMethodDefinition(ed.GetAccessors().Adder).Attributes),
                attributes = AttributeNames(ed.GetCustomAttributes()),
                summary = docs.ForMember("E:" + Names.StripArity(DocTypeName(declaringType)) + "." + DocMemberName(name)),
            };
        }
        catch (Exception ex) { return new { name, error = ex.Message }; }
    }

    // ---- helpers ----------------------------------------------------------

    private ImmutableArray<string> GenericParameterNames(GenericParameterHandleCollection handles)
    {
        if (handles.Count == 0) return ImmutableArray<string>.Empty;
        var builder = ImmutableArray.CreateBuilder<string>(handles.Count);
        foreach (var h in handles)
            builder.Add(reader.GetString(reader.GetGenericParameter(h).Name));
        return builder.MoveToImmutable();
    }

    private string EntityTypeName(EntityHandle handle, GenericContext ctx, bool docId)
    {
        var provider = docId ? _docId : _display;
        var separator = docId ? '.' : '+';
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => Names.OfTypeDef(reader, (TypeDefinitionHandle)handle, separator),
            HandleKind.TypeReference => Names.OfTypeRef(reader, (TypeReferenceHandle)handle, separator),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, ctx),
            _ => "System.Object",
        };
    }

    private static string DocTypeName(string displayFullName) => displayFullName.Replace('+', '.');

    /// <summary>Doc IDs escape the dots of <c>.ctor</c> and of explicit interface implementations as <c>#</c>.</summary>
    private static string DocMemberName(string name) => name.Replace('.', '#');

    private string MethodDocId(string declaringType, string name, int genericArity, MethodSignature<string> docSignature)
    {
        var sb = new StringBuilder("M:");
        sb.Append(Names.StripArity(DocTypeName(declaringType))).Append('.').Append(DocMemberName(name));
        if (genericArity > 0) sb.Append("``").Append(genericArity.ToString(CultureInfo.InvariantCulture));
        if (docSignature.ParameterTypes.Length > 0)
            sb.Append('(').Append(string.Join(",", docSignature.ParameterTypes)).Append(')');
        if (name is "op_Implicit" or "op_Explicit")
            sb.Append('~').Append(docSignature.ReturnType);
        return sb.ToString();
    }

    private static string FormatSignature(
        MethodDefinition md, string name, bool isCtor, string declaringType,
        MethodSignature<string> signature, ImmutableArray<string> genericParameters, List<object> parameters)
    {
        var sb = new StringBuilder();
        sb.Append(Access(md.Attributes)).Append(' ');
        if ((md.Attributes & MethodAttributes.Static) != 0) sb.Append("static ");
        if (isCtor)
        {
            var simple = declaringType;
            var lastDot = simple.LastIndexOfAny(['.', '+']);
            sb.Append(Names.StripArity(lastDot >= 0 ? simple[(lastDot + 1)..] : simple));
        }
        else
        {
            sb.Append(signature.ReturnType).Append(' ').Append(name);
            if (genericParameters.Length > 0)
                sb.Append('<').Append(string.Join(", ", genericParameters)).Append('>');
        }

        sb.Append('(');
        sb.Append(string.Join(", ", parameters.Select((p, i) =>
        {
            dynamic d = p;
            string type = d.type;
            string modifier = d.modifier;
            if (modifier.Length > 0 && type.EndsWith("&", StringComparison.Ordinal)) type = type[..^1];
            var text = (modifier.Length > 0 ? modifier + " " : "") + type + " " + d.name;
            if (d.@default is string def) text += " = " + def;
            return text;
        })));
        sb.Append(')');
        return sb.ToString();
    }

    private string? ConstantValue(ConstantHandle handle)
    {
        if (handle.IsNil) return null;
        try
        {
            var constant = reader.GetConstant(handle);
            var blob = reader.GetBlobReader(constant.Value);
            return constant.TypeCode switch
            {
                ConstantTypeCode.Boolean => blob.ReadBoolean() ? "true" : "false",
                ConstantTypeCode.Char => "'" + blob.ReadChar() + "'",
                ConstantTypeCode.SByte => blob.ReadSByte().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Byte => blob.ReadByte().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int16 => blob.ReadInt16().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt16 => blob.ReadUInt16().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int32 => blob.ReadInt32().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt32 => blob.ReadUInt32().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int64 => blob.ReadInt64().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt64 => blob.ReadUInt64().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Single => blob.ReadSingle().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Double => blob.ReadDouble().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.String => "\"" + blob.ReadUTF16(blob.RemainingBytes) + "\"",
                ConstantTypeCode.NullReference => "null",
                _ => null,
            };
        }
        catch { return null; }
    }

    private List<string> AttributeNames(CustomAttributeHandleCollection handles)
    {
        var result = new List<string>();
        foreach (var handle in handles)
        {
            var name = AttributeTypeName(handle);
            if (name is null) continue;
            if (name is "System.Runtime.CompilerServices.NullableAttribute"
                     or "System.Runtime.CompilerServices.NullableContextAttribute"
                     or "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
                continue;
            result.Add(name);
        }
        return result;
    }

    private string? AttributeTypeName(CustomAttributeHandle handle)
    {
        try
        {
            var ctor = reader.GetCustomAttribute(handle).Constructor;
            switch (ctor.Kind)
            {
                case HandleKind.MethodDefinition:
                    return Names.OfTypeDef(reader, reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(), '+');
                case HandleKind.MemberReference:
                    var parent = reader.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                    return parent.Kind switch
                    {
                        HandleKind.TypeReference => Names.OfTypeRef(reader, (TypeReferenceHandle)parent, '+'),
                        HandleKind.TypeDefinition => Names.OfTypeDef(reader, (TypeDefinitionHandle)parent, '+'),
                        _ => null,
                    };
                default: return null;
            }
        }
        catch { return null; }
    }

    // ---- nullability ------------------------------------------------------
    // 0 = oblivious, 1 = not annotated (non-nullable), 2 = annotated (nullable).

    private byte NullableContext(CustomAttributeHandleCollection attributes, Func<byte> fallback)
    {
        var local = ReadNullableAttribute(attributes, "System.Runtime.CompilerServices.NullableContextAttribute");
        return local ?? fallback();
    }

    private byte TypeNullableContext(TypeDefinitionHandle handle)
    {
        for (var current = handle; !current.IsNil;)
        {
            var td = reader.GetTypeDefinition(current);
            var value = ReadNullableAttribute(td.GetCustomAttributes(), "System.Runtime.CompilerServices.NullableContextAttribute");
            if (value is not null) return value.Value;
            current = td.IsNested ? td.GetDeclaringType() : default;
        }
        return 0;
    }

    private string? Nullability(string type, CustomAttributeHandleCollection? memberAttributes, byte context)
    {
        // Value types (other than Nullable<T>) carry no annotation; nullability is structural.
        if (IsKnownValueType(type)) return null;

        byte flag = context;
        if (memberAttributes is { } attrs)
        {
            var value = ReadNullableAttribute(attrs, "System.Runtime.CompilerServices.NullableAttribute");
            if (value is not null) flag = value.Value;
        }

        return flag switch { 1 => "notnull", 2 => "nullable", _ => "oblivious" };
    }

    private static bool IsKnownValueType(string type)
    {
        if (type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("]", StringComparison.Ordinal)) return false;
        if (type.StartsWith("System.Nullable<", StringComparison.Ordinal)) return false;
        return type is "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char" or "System.Int16"
            or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64"
            or "System.Single" or "System.Double" or "System.Decimal" or "System.IntPtr" or "System.UIntPtr"
            or "System.Void" or "System.DateTime" or "System.TimeSpan" or "System.Guid";
    }

    /// <summary>
    /// Reads the leading byte of a NullableAttribute / NullableContextAttribute blob without
    /// needing the attribute's own assembly. Blob layout: 0x0001 prolog, fixed args, named-arg count.
    /// </summary>
    private byte? ReadNullableAttribute(CustomAttributeHandleCollection handles, string attributeName)
    {
        foreach (var handle in handles)
        {
            if (AttributeTypeName(handle) != attributeName) continue;
            try
            {
                var blob = reader.GetBlobBytes(reader.GetCustomAttribute(handle).Value);
                if (blob.Length == 5) return blob[2];               // ctor(byte)
                if (blob.Length >= 9) return blob[6];               // ctor(byte[]): 2 prolog + 4 length
            }
            catch { }
            return null;
        }
        return null;
    }
}

internal sealed class XmlDocs
{
    private readonly Dictionary<string, string> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _byNameKey = new(StringComparer.Ordinal);

    private XmlDocs() { }

    public bool Present { get; private set; }

    /// <summary>True when the file was not well-formed XML and summaries were recovered by scanning.</summary>
    public bool Recovered { get; private set; }

    public int Count => _byId.Count;

    public static XmlDocs Load(byte[]? xmlBytes)
    {
        var d = new XmlDocs();
        if (xmlBytes is null || xmlBytes.Length == 0) return d;
        d.Present = true;

        var text = DecodeText(xmlBytes);
        if (d.TryLoadWellFormed(text)) return d;

        // Doc files are routinely emitted with unescaped '<' (F# generic syntax such as
        // Result<'T>, hand-written angle brackets), which makes the whole document unparseable.
        // Recover per-member rather than losing every summary in the package.
        d.Recovered = true;
        d.LoadByScanning(text);
        return d;
    }

    /// <summary>
    /// Exact doc-ID lookup, falling back to a name-only match when a member's name is unambiguous.
    /// The fallback covers signature-formatting corner cases (nested generics, compiler-specific
    /// ID spellings) that would otherwise silently lose a summary.
    /// </summary>
    public string? ForMember(string id)
    {
        if (_byId.TryGetValue(id, out var exact)) return exact;
        if (_byNameKey.TryGetValue(NameKey(id), out var candidates) && candidates.Count == 1)
            return _byId[candidates[0]];
        return null;
    }

    private bool TryLoadWellFormed(string text)
    {
        try
        {
            var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            foreach (var member in doc.Descendants("member"))
            {
                var id = member.Attribute("name")?.Value;
                var summary = member.Element("summary");
                if (string.IsNullOrEmpty(id) || summary is null) continue;
                Add(id, Render(summary));
            }
            return true;
        }
        catch (System.Xml.XmlException)
        {
            _byId.Clear();
            _byNameKey.Clear();
            return false;
        }
    }

    private static readonly Regex MemberRegex = new(
        """<member\s+name\s*=\s*"(?<id>[^"]*)"\s*>(?<body>.*?)</member\s*>""",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SummaryRegex = new(
        """<summary\s*>(?<text>.*?)</summary\s*>""",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CrefRegex = new(
        """<(?:see|seealso)\s[^>]*?(?:cref|langword|href)\s*=\s*"(?<ref>[^"]*)"[^>]*?>""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameRefRegex = new(
        """<(?:paramref|typeparamref)\s[^>]*?name\s*=\s*"(?<name>[^"]*)"[^>]*?>""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Only strip tags that are actually XML-doc markup. Anything else (Result<'T>, List<int>)
    // is literal prose the author failed to escape and must survive.
    private static readonly Regex DocTagRegex = new(
        """</?(?:summary|remarks|para|c|code|list|item|term|description|see|seealso|paramref|typeparamref|param|typeparam|returns|value|example|exception|b|i|br|inheritdoc|note|a)(?:\s[^>]*)?/?>""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private void LoadByScanning(string text)
    {
        foreach (Match member in MemberRegex.Matches(text))
        {
            var id = member.Groups["id"].Value;
            if (id.Length == 0) continue;
            var summary = SummaryRegex.Match(member.Groups["body"].Value);
            if (!summary.Success) continue;
            Add(id, RenderRaw(summary.Groups["text"].Value));
        }
    }

    private void Add(string id, string summary)
    {
        if (summary.Length == 0) return;
        _byId[id] = summary;

        var key = NameKey(id);
        if (!_byNameKey.TryGetValue(key, out var list))
            _byNameKey[key] = list = [];
        list.Add(id);
    }

    /// <summary>Flattens doc markup to prose, keeping cref targets and paramref names visible.</summary>
    private static string Render(XElement element)
    {
        var sb = new StringBuilder();
        RenderNodes(element.Nodes(), sb);
        return Normalize(sb.ToString());
    }

    private static void RenderNodes(IEnumerable<XNode> nodes, StringBuilder sb)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement e when e.Name.LocalName is "see" or "seealso":
                    var target = e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? e.Attribute("href")?.Value;
                    if (!string.IsNullOrEmpty(target)) sb.Append(StripIdPrefix(target));
                    else RenderNodes(e.Nodes(), sb);
                    break;
                case XElement e when e.Name.LocalName is "paramref" or "typeparamref":
                    sb.Append(e.Attribute("name")?.Value);
                    break;
                case XElement e:
                    RenderNodes(e.Nodes(), sb);
                    break;
            }
        }
    }

    private static string RenderRaw(string fragment)
    {
        fragment = CrefRegex.Replace(fragment, m => StripIdPrefix(m.Groups["ref"].Value));
        fragment = NameRefRegex.Replace(fragment, m => m.Groups["name"].Value);
        fragment = DocTagRegex.Replace(fragment, "");
        return Normalize(Unescape(fragment));
    }

    /// <summary>Turns a doc-comment cref such as <c>T:System.String</c> into <c>System.String</c>.</summary>
    private static string StripIdPrefix(string cref) =>
        cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;

    private static string Unescape(string s) => s
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal);

    private static string DecodeText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Member identity ignoring how parameter *types* are spelled, but keeping how many there are.
    /// Two overloads therefore never collapse onto one key, so the fallback in
    /// <see cref="ForMember"/> cannot attribute one overload's summary to another.
    /// </summary>
    private static string NameKey(string id)
    {
        var paren = id.IndexOf('(');
        var parameterCount = 0;
        if (paren >= 0)
        {
            var close = id.LastIndexOf(')');
            var argumentList = close > paren ? id[(paren + 1)..close] : id[(paren + 1)..];
            parameterCount = CountTopLevelArguments(argumentList);
            id = id[..paren];
        }

        var genericArity = id.IndexOf("``", StringComparison.Ordinal);
        if (genericArity >= 0) id = id[..genericArity];
        return id + "#" + parameterCount.ToString(CultureInfo.InvariantCulture);
    }

    private static int CountTopLevelArguments(string argumentList)
    {
        if (argumentList.Length == 0) return 0;
        var count = 1;
        var depth = 0;
        foreach (var c in argumentList)
        {
            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    private static string Normalize(string s)
    {
        var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join(" ", lines).Trim();
    }
}

internal sealed class SourceLinkInfo
{
    public Dictionary<string, string> UrlMap { get; } = new();
}

internal static class SourceLinkReader
{
    public static SourceLinkInfo? TryRead(PEReader peReader)
    {
        try
        {
            // Only embedded PDBs are reachable; side-by-side .pdb files are not packaged in nupkgs.
            foreach (var entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb) continue;
                using var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                return ReadFromPdb(provider.GetMetadataReader());
            }
        }
        catch { }
        return null;
    }

    private static SourceLinkInfo? ReadFromPdb(MetadataReader reader)
    {
        var info = new SourceLinkInfo();
        var sourceLinkGuid = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");

        foreach (var cdiHandle in reader.CustomDebugInformation)
        {
            var cdi = reader.GetCustomDebugInformation(cdiHandle);
            if (reader.GetGuid(cdi.Kind) != sourceLinkGuid) continue;

            var json = Encoding.UTF8.GetString(reader.GetBlobBytes(cdi.Value));
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("documents", out var documents))
                {
                    foreach (var entry in documents.EnumerateObject())
                        info.UrlMap[entry.Name] = entry.Value.GetString() ?? "";
                }
            }
            catch { }
        }

        return info.UrlMap.Count == 0 ? null : info;
    }
}

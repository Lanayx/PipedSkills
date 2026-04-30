#:package NuGet.Protocol@7.3.1
#:package System.Reflection.MetadataLoadContext@10.0.7
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
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
            "Usage: nuget-public-api inspect <packageId> [--version <ver>] [--tfm <tfm>] " +
            "[--source <feed-url>] [--output <file>] [--summary] [--include-internal] " +
            "[--max-members-per-type N] [--no-source-link]\n" +
            "Output: JSON describing the package's public API. Defaults to stdout.\n" +
            "All package processing is done in memory; no temp files are written.");
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
    bool SummaryOnly)
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
                default:
                    Console.Error.WriteLine("Unknown arg: " + args[i]);
                    return null;
            }
        }

        return new CliArgs(cmd, packageId, version, tfm, source, output,
            includeInternal, maxMembers, noSourceLink, summaryOnly);
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
        foreach (var entry in chosenGroup.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
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

        // Walk dependency graph in memory (best-effort)
        var dependencyAssemblies = await ResolveDependencyAssembliesAsync(
            packageReader, chosenGroup.TargetFramework, sourceRepo, sourceCacheContext, logger, ct);

        // Source Link map (best effort, per assembly)
        var sourceLinkMaps = new Dictionary<string, SourceLinkInfo?>();
        if (!args.NoSourceLink)
        {
            foreach (var asm in primaryAssemblies)
                sourceLinkMaps[asm.Key] = SourceLinkReader.TryRead(asm.Dll);
        }

        // Build in-memory MetadataLoadContext
        var inMemoryByName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in primaryAssemblies)
            inMemoryByName[Path.GetFileNameWithoutExtension(a.Key)] = a.Dll;
        foreach (var a in dependencyAssemblies)
            inMemoryByName.TryAdd(Path.GetFileNameWithoutExtension(a.Key), a.Dll);

        var runtimeRefs = GetRuntimeRefAssemblies();
        var resolver = new InMemoryAssemblyResolver(inMemoryByName, runtimeRefs);
        var coreAssemblyName = GetCoreAssemblyName(runtimeRefs);
        using var mlc = new MetadataLoadContext(resolver, coreAssemblyName);

        // Inspect assemblies via MetadataLoadContext
        var apiAssemblies = new List<object>();
        foreach (var primary in primaryAssemblies)
        {
            var xmlDocs = XmlDocs.Load(primary.Xml);

            Assembly asm;
            try { asm = mlc.LoadFromByteArray(primary.Dll); }
            catch (Exception ex)
            {
                apiAssemblies.Add(new { file = primary.Key, error = ex.Message });
                continue;
            }

            var sourceLink = sourceLinkMaps.GetValueOrDefault(primary.Key);
            var asmModel = ApiExtractor.Extract(asm, xmlDocs, args, sourceLink, primary.Key);
            apiAssemblies.Add(asmModel);
        }

        // Dependencies summary
        var depGroups = nuspec.GetDependencyGroups().ToList();
        var depBest = NuGetFrameworkUtility.GetNearest(depGroups, chosenGroup.TargetFramework, g => g.TargetFramework);
        var dependencies = (depBest?.Packages ?? Enumerable.Empty<PackageDependency>())
            .Select(p => new { id = p.Id, range = p.VersionRange.ToShortString() })
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
            assemblies = apiAssemblies,
            notes = new
            {
                apiSource = refGroups.Count > 0 ? "ref/" : "lib/",
                sourceLinkAvailable = sourceLinkMaps.Values.Any(v => v is not null),
                hint = "Public API extracted from compiled assembly metadata. All work is performed in memory; no temp files written.",
            }
        };
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

    private static async Task<List<InMemoryAssembly>> ResolveDependencyAssembliesAsync(
        PackageArchiveReader rootPkg,
        NuGetFramework tfm,
        SourceRepository sourceRepo,
        SourceCacheContext cache,
        ILogger logger,
        CancellationToken ct)
    {
        var result = new List<InMemoryAssembly>();
        var depGroups = rootPkg.NuspecReader.GetDependencyGroups().ToList();
        var nearest = NuGetFrameworkUtility.GetNearest(depGroups, tfm, g => g.TargetFramework);
        if (nearest is null) return result;

        var findResource = await sourceRepo.GetResourceAsync<FindPackageByIdResource>(ct);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task Walk(string id, NuGet.Versioning.VersionRange range)
        {
            if (!visited.Add(id)) return;
            try
            {
                var versions = await findResource.GetAllVersionsAsync(id, cache, logger, ct);
                var best = range.FindBestMatch(versions);
                if (best is null) return;

                var nupkgBytes = await DownloadNupkgAsync(findResource, id, best, cache, logger, ct);
                using var pkg = new PackageArchiveReader(new MemoryStream(nupkgBytes, writable: false));

                var refItems = (await pkg.GetReferenceItemsAsync(ct)).ToList();
                var libItems = (await pkg.GetLibItemsAsync(ct)).ToList();
                var src2 = refItems.Count > 0 ? refItems : libItems;
                var grp = NuGetFrameworkUtility.GetNearest(src2, tfm, g => g.TargetFramework);
                if (grp is not null)
                {
                    foreach (var entry in grp.Items.Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        var dll = await ReadEntryBytesAsync(pkg, entry, ct);
                        if (dll is null) continue;
                        var xmlEntry = Path.ChangeExtension(entry, ".xml").Replace('\\', '/');
                        var xml = await ReadEntryBytesAsync(pkg, xmlEntry, ct);
                        result.Add(new InMemoryAssembly(Path.GetFileName(entry), dll, xml));
                    }
                }

                var depGroups2 = pkg.NuspecReader.GetDependencyGroups().ToList();
                var nearest2 = NuGetFrameworkUtility.GetNearest(depGroups2, tfm, g => g.TargetFramework);
                if (nearest2 is not null)
                {
                    foreach (var p in nearest2.Packages)
                        await Walk(p.Id, p.VersionRange);
                }
            }
            catch
            {
                // best-effort
            }
        }

        foreach (var p in nearest.Packages)
            await Walk(p.Id, p.VersionRange);

        return result;
    }

    private static IEnumerable<string> GetRuntimeRefAssemblies()
    {
        // Use the running runtime's reference assemblies as a baseline mscorlib provider.
        var trustedRaw = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(trustedRaw)) return Array.Empty<string>();
        return trustedRaw.Split(Path.PathSeparator).Where(File.Exists);
    }

    private static string GetCoreAssemblyName(IEnumerable<string> paths)
    {
        // Prefer System.Runtime, fallback System.Private.CoreLib, fallback mscorlib
        foreach (var preferred in new[] { "System.Runtime.dll", "System.Private.CoreLib.dll", "mscorlib.dll", "netstandard.dll" })
        {
            if (paths.Any(p => string.Equals(Path.GetFileName(p), preferred, StringComparison.OrdinalIgnoreCase)))
                return Path.GetFileNameWithoutExtension(preferred);
        }
        return "System.Runtime";
    }
}

/// <summary>
/// Resolves assemblies for MetadataLoadContext from an in-memory dictionary first,
/// then falls back to the running runtime's reference assemblies (TPA) by file path.
/// </summary>
internal sealed class InMemoryAssemblyResolver : MetadataAssemblyResolver
{
    private readonly Dictionary<string, byte[]> _byName;
    private readonly Dictionary<string, string> _runtimePathsByName;

    public InMemoryAssemblyResolver(Dictionary<string, byte[]> byName, IEnumerable<string> runtimePaths)
    {
        _byName = byName;
        _runtimePathsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in runtimePaths)
        {
            var simple = Path.GetFileNameWithoutExtension(p);
            if (!_runtimePathsByName.ContainsKey(simple))
                _runtimePathsByName[simple] = p;
        }
    }

    public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name)) return null;

        if (_byName.TryGetValue(name, out var bytes))
            return context.LoadFromByteArray(bytes);

        if (_runtimePathsByName.TryGetValue(name, out var path))
            return context.LoadFromAssemblyPath(path);

        return null;
    }
}

internal static class ApiExtractor
{
    public static object Extract(Assembly asm, XmlDocs docs, CliArgs args, SourceLinkInfo? sourceLink, string fileName)
    {
        Type[] allTypes;
        try { allTypes = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(t => t is not null).ToArray()!; }

        var types = allTypes
            .Where(t => IsVisible(t, args.IncludeInternal))
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .ToList();

        var byNs = types.GroupBy(t => t.Namespace ?? "")
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                @namespace = g.Key,
                types = args.SummaryOnly
                    ? (IEnumerable<object>)g.Select(t => (object)new
                        {
                            name = TypeName(t),
                            kind = TypeKind(t),
                            summary = docs.ForMember(XmlDocs.TypeId(t)),
                        }).ToList()
                    : g.Select(t => SafeDescribe(t, docs, args, sourceLink)).ToList(),
            })
            .ToList();

        return new
        {
            file = fileName,
            assemblyName = asm.GetName().Name,
            assemblyVersion = asm.GetName().Version?.ToString(),
            sourceLink = sourceLink is null ? null : new
            {
                hasPdb = true,
                documentMappings = sourceLink.UrlMap.Take(8).Select(kv => new { local = kv.Key, url = kv.Value }).ToList(),
            },
            namespaces = byNs,
        };
    }

    private static bool IsVisible(Type t, bool includeInternal)
    {
        if (t.IsNested)
        {
            // Public, protected, and protected internal nested types are always part of public surface.
            if (t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem)
                return Visible(t.DeclaringType!, includeInternal);
            if (includeInternal && (t.IsNestedAssembly || t.IsNestedFamANDAssem))
                return Visible(t.DeclaringType!, includeInternal);
            return false;
        }
        return t.IsPublic || (includeInternal && t.IsNotPublic);
    }

    private static bool Visible(Type t, bool includeInternal) => IsVisible(t, includeInternal);

    private static object SafeDescribe(Type t, XmlDocs docs, CliArgs args, SourceLinkInfo? sl)
    {
        try { return DescribeType(t, docs, args, sl); }
        catch (Exception ex) { return new { name = t.Name, fullName = t.FullName, error = ex.Message }; }
    }

    private static object DescribeType(Type t, XmlDocs docs, CliArgs args, SourceLinkInfo? sl)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;

        IEnumerable<MemberInfo> members;
        try { members = t.GetMembers(flags).Where(m => IsMemberVisible(m, args.IncludeInternal)); }
        catch { members = Array.Empty<MemberInfo>(); }

        var ctors = new List<object>();
        var methods = new List<object>();
        var properties = new List<object>();
        var fields = new List<object>();
        var events = new List<object>();
        var nestedTypes = new List<object>();

        int cap = args.MaxMembersPerType > 0 ? args.MaxMembersPerType : int.MaxValue;

        foreach (var m in members)
        {
            try
            {
            switch (m)
            {
                case ConstructorInfo ci:
                    if (ctors.Count < cap) ctors.Add(DescribeMethod(ci, docs, sl, isCtor: true));
                    break;
                case MethodInfo mi when !mi.IsSpecialName:
                    if (methods.Count < cap) methods.Add(DescribeMethod(mi, docs, sl));
                    break;
                case PropertyInfo pi:
                    if (properties.Count < cap) properties.Add(DescribeProperty(pi, docs, args.IncludeInternal));
                    break;
                case FieldInfo fi:
                    if (fields.Count < cap) fields.Add(DescribeField(fi, docs));
                    break;
                case EventInfo ei:
                    if (events.Count < cap) events.Add(new
                    {
                        name = ei.Name,
                        handlerType = TypeName(ei.EventHandlerType!),
                        summary = docs.ForMember(XmlDocs.EventId(ei)),
                    });
                    break;
                case Type nt:
                    if (nestedTypes.Count < cap) nestedTypes.Add(new { name = nt.Name, kind = TypeKind(nt) });
                    break;
            }
            }
            catch { /* skip member that can't be inspected */ }
        }

        return new
        {
            name = TypeName(t),
            fullName = t.FullName,
            kind = TypeKind(t),
            isStatic = t.IsAbstract && t.IsSealed,
            isAbstract = t.IsAbstract && !t.IsSealed && !t.IsInterface,
            isSealed = t.IsSealed && !t.IsAbstract,
            baseType = t.BaseType is null || t.BaseType == typeof(object) ? null : TypeName(t.BaseType),
            interfaces = t.GetInterfaces().Select(TypeName).ToList(),
            genericParameters = t.IsGenericTypeDefinition
                ? t.GetGenericArguments().Select(g => g.Name).ToList()
                : null,
            attributes = AttributeNames(t.GetCustomAttributesData()),
            summary = docs.ForMember(XmlDocs.TypeId(t)),
            constructors = ctors,
            methods,
            properties,
            fields,
            events,
            nestedTypes,
        };
    }

    private static bool IsMemberVisible(MemberInfo m, bool includeInternal)
    {
        return m switch
        {
            FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly || (includeInternal && (f.IsAssembly || f.IsFamilyAndAssembly)),
            MethodBase mb => mb.IsPublic || mb.IsFamily || mb.IsFamilyOrAssembly || (includeInternal && (mb.IsAssembly || mb.IsFamilyAndAssembly)),
            PropertyInfo p => (p.GetMethod is { } gm && IsMemberVisible(gm, includeInternal)) ||
                              (p.SetMethod is { } sm && IsMemberVisible(sm, includeInternal)),
            EventInfo e => e.AddMethod is { } am && IsMemberVisible(am, includeInternal),
            Type t => IsVisible(t, includeInternal),
            _ => false,
        };
    }

    private static object DescribeMethod(MethodBase mb, XmlDocs docs, SourceLinkInfo? sl, bool isCtor = false)
    {
        var typeContext = NullabilityHelper.GetTypeContext(mb.DeclaringType);
        var methodContext = NullabilityHelper.GetContext(mb.GetCustomAttributesData(), typeContext);

        var parameters = mb.GetParameters().Select(p => new
        {
            name = p.Name,
            type = TypeName(p.ParameterType),
            optional = p.IsOptional,
            @default = p.HasDefaultValue ? p.RawDefaultValue?.ToString() : null,
            modifier = ParamModifier(p),
            nullability = NullabilityHelper.Describe(p.ParameterType, p.GetCustomAttributesData(), methodContext),
        }).ToList();

        var mi = mb as MethodInfo;
        return new
        {
            name = isCtor ? ".ctor" : mb.Name,
            access = AccessModifier(mb),
            isStatic = mb.IsStatic,
            isAbstract = mb.IsAbstract,
            isVirtual = mb.IsVirtual && !mb.IsFinal,
            isOverride = mi is not null && (mi.Attributes & MethodAttributes.Virtual) != 0 && (mi.Attributes & MethodAttributes.NewSlot) == 0,
            returnType = mi is null ? null : TypeName(mi.ReturnType),
            returnNullability = mi is null ? null
                : NullabilityHelper.Describe(mi.ReturnType, mi.ReturnParameter.GetCustomAttributesData(), methodContext),
            genericParameters = mb.IsGenericMethodDefinition
                ? mb.GetGenericArguments().Select(g => g.Name).ToList()
                : null,
            parameters,
            attributes = AttributeNames(mb.GetCustomAttributesData()),
            summary = docs.ForMember(XmlDocs.MethodId(mb, isCtor)),
            signature = MethodSignature(mb, isCtor),
        };
    }

    private static object DescribeProperty(PropertyInfo p, XmlDocs docs, bool includeInternal)
    {
        var getter = p.GetMethod;
        var setter = p.SetMethod;
        var primary = (getter ?? setter)!;
        return new
        {
            name = p.Name,
            type = TypeName(p.PropertyType),
            canRead = getter is not null,
            canWrite = setter is not null,
            getterAccess = getter is null ? null : AccessModifier(getter),
            setterAccess = setter is null ? null : AccessModifier(setter),
            isStatic = primary.IsStatic,
            access = AccessModifier(primary),
            indexerParameters = p.GetIndexParameters().Length == 0 ? null
                : p.GetIndexParameters().Select(ip => new { name = ip.Name, type = TypeName(ip.ParameterType) }).ToList(),
            attributes = AttributeNames(p.GetCustomAttributesData()),
            summary = docs.ForMember(XmlDocs.PropertyId(p)),
        };
    }

    private static object DescribeField(FieldInfo f, XmlDocs docs)
    {
        return new
        {
            name = f.Name,
            type = TypeName(f.FieldType),
            isStatic = f.IsStatic,
            isReadOnly = f.IsInitOnly,
            isConst = f.IsLiteral,
            constantValue = f.IsLiteral ? SafeRawConstant(f) : null,
            access = AccessModifier(f),
            attributes = AttributeNames(f.GetCustomAttributesData()),
            summary = docs.ForMember(XmlDocs.FieldId(f)),
        };
    }

    private static string? SafeRawConstant(FieldInfo f)
    {
        try { return f.GetRawConstantValue()?.ToString(); }
        catch { return null; }
    }

    private static List<string> AttributeNames(IList<CustomAttributeData> data)
        => data.Select(a => a.AttributeType.FullName ?? a.AttributeType.Name)
            .Where(n => n != "System.Runtime.CompilerServices.NullableAttribute" &&
                        n != "System.Runtime.CompilerServices.NullableContextAttribute" &&
                        n != "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
            .ToList();

    private static string AccessModifier(MethodBase m) =>
        m.IsPublic ? "public" :
        m.IsFamily ? "protected" :
        m.IsFamilyOrAssembly ? "protected internal" :
        m.IsAssembly ? "internal" :
        m.IsFamilyAndAssembly ? "private protected" : "private";

    private static string AccessModifier(FieldInfo f) =>
        f.IsPublic ? "public" :
        f.IsFamily ? "protected" :
        f.IsFamilyOrAssembly ? "protected internal" :
        f.IsAssembly ? "internal" :
        f.IsFamilyAndAssembly ? "private protected" : "private";

    private static string TypeKind(Type t) =>
        t.IsInterface ? "interface" :
        t.IsEnum ? "enum" :
        t.IsValueType ? "struct" :
        typeof(MulticastDelegate).IsAssignableFrom(t.BaseType) ? "delegate" :
        (t.IsAbstract && t.IsSealed) ? "static class" : "class";

    private static string ParamModifier(ParameterInfo p)
    {
        if (p.IsOut) return "out";
        if (p.ParameterType.IsByRef) return p.IsIn ? "in" : "ref";
        return "";
    }

    public static string TypeName(Type t)
    {
        if (t.IsByRef) return TypeName(t.GetElementType()!) + "&";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (t.IsGenericParameter) return t.Name;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var name = def.FullName ?? def.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            var argList = string.Join(", ", t.GetGenericArguments().Select(TypeName));
            return $"{name}<{argList}>";
        }
        return t.FullName ?? t.Name;
    }

    private static string MethodSignature(MethodBase mb, bool isCtor)
    {
        var sb = new StringBuilder();
        sb.Append(AccessModifier(mb)).Append(' ');
        if (mb.IsStatic) sb.Append("static ");
        if (mb is MethodInfo mi)
        {
            sb.Append(TypeName(mi.ReturnType)).Append(' ');
            sb.Append(mi.Name);
            if (mi.IsGenericMethodDefinition)
                sb.Append('<').Append(string.Join(", ", mi.GetGenericArguments().Select(g => g.Name))).Append('>');
        }
        else
        {
            sb.Append(isCtor ? mb.DeclaringType?.Name ?? ".ctor" : mb.Name);
        }
        sb.Append('(');
        sb.Append(string.Join(", ", mb.GetParameters().Select(p =>
        {
            var mod = ParamModifier(p);
            var s = (mod.Length > 0 ? mod + " " : "") + TypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name;
            if (p.HasDefaultValue) s += " = " + (p.RawDefaultValue?.ToString() ?? "null");
            return s;
        })));
        sb.Append(')');
        return sb.ToString();
    }
}

internal sealed class XmlDocs
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private XmlDocs() { }

    public static XmlDocs Load(byte[]? xmlBytes)
    {
        var d = new XmlDocs();
        if (xmlBytes is null || xmlBytes.Length == 0) return d;
        try
        {
            using var ms = new MemoryStream(xmlBytes, writable: false);
            var doc = XDocument.Load(ms);
            foreach (var m in doc.Descendants("member"))
            {
                var name = m.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                var summary = m.Element("summary")?.Value?.Trim();
                if (!string.IsNullOrEmpty(summary))
                    d._map[name] = Normalize(summary);
            }
        }
        catch { }
        return d;
    }

    public string? ForMember(string id) => _map.TryGetValue(id, out var v) ? v : null;

    private static string Normalize(string s)
    {
        var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join(" ", lines);
    }

    public static string TypeId(Type t) => "T:" + ToDocFullName(t);
    public static string FieldId(FieldInfo f) => "F:" + ToDocFullName(f.DeclaringType!) + "." + f.Name;
    public static string PropertyId(PropertyInfo p)
    {
        var idx = p.GetIndexParameters();
        var s = "P:" + ToDocFullName(p.DeclaringType!) + "." + p.Name;
        if (idx.Length > 0) s += "(" + string.Join(",", idx.Select(i => ToDocFullName(i.ParameterType))) + ")";
        return s;
    }
    public static string EventId(EventInfo e) => "E:" + ToDocFullName(e.DeclaringType!) + "." + e.Name;
    public static string MethodId(MethodBase m, bool isCtor)
    {
        var name = isCtor ? "#ctor" : m.Name;
        var sb = new StringBuilder("M:");
        sb.Append(ToDocFullName(m.DeclaringType!)).Append('.').Append(name);
        if (m is MethodInfo mi && mi.IsGenericMethodDefinition)
            sb.Append("``").Append(mi.GetGenericArguments().Length);
        var ps = m.GetParameters();
        if (ps.Length > 0)
            sb.Append('(').Append(string.Join(",", ps.Select(p => ToDocFullName(p.ParameterType)))).Append(')');
        return sb.ToString();
    }

    private static string ToDocFullName(Type t)
    {
        if (t.IsByRef) return ToDocFullName(t.GetElementType()!) + "@";
        if (t.IsArray) return ToDocFullName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (t.IsGenericParameter)
            return t.DeclaringMethod is not null ? "``" + t.GenericParameterPosition : "`" + t.GenericParameterPosition;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var name = def.FullName ?? def.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            var args = string.Join(",", t.GetGenericArguments().Select(ToDocFullName));
            return name + "{" + args + "}";
        }
        return (t.FullName ?? t.Name).Replace('+', '.');
    }
}

internal sealed class SourceLinkInfo
{
    public Dictionary<string, string> UrlMap { get; } = new();
}

internal static class NullabilityHelper
{
    // Per ECMA / Roslyn conventions:
    // 0 = oblivious (no annotation context, e.g. legacy code)
    // 1 = not annotated (non-nullable)
    // 2 = annotated   (nullable)

    public static byte GetTypeContext(Type? t)
    {
        if (t is null) return 0;
        // Walk up nested types and assembly for default context
        for (var cur = t; cur is not null; cur = cur.DeclaringType)
        {
            var ctx = GetContext(cur.GetCustomAttributesData(), 0);
            if (ctx != 0) return ctx;
        }
        try
        {
            var asmCtx = GetContext(t.Assembly.GetCustomAttributesData(), 0);
            if (asmCtx != 0) return asmCtx;
        }
        catch { }
        return 0;
    }

    public static byte GetContext(IList<CustomAttributeData> attrs, byte fallback)
    {
        foreach (var a in attrs)
        {
            if (a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute"
                && a.ConstructorArguments.Count == 1
                && a.ConstructorArguments[0].Value is byte b)
                return b;
        }
        return fallback;
    }

    public static string? Describe(Type t, IList<CustomAttributeData> memberAttrs, byte context)
    {
        // Value types that are not Nullable<T> are always non-nullable; skip
        if (t.IsValueType && !(t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1"))
            return null;

        byte flag = context;

        foreach (var a in memberAttrs)
        {
            if (a.AttributeType.FullName != "System.Runtime.CompilerServices.NullableAttribute") continue;
            if (a.ConstructorArguments.Count != 1) continue;
            var v = a.ConstructorArguments[0].Value;
            if (v is byte single) flag = single;
            else if (v is System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument> arr && arr.Count > 0
                && arr[0].Value is byte first) flag = first;
            break;
        }

        return flag switch
        {
            1 => "notnull",
            2 => "nullable",
            _ => "oblivious",
        };
    }
}

internal static class SourceLinkReader
{
    public static SourceLinkInfo? TryRead(byte[] peBytes)
    {
        try
        {
            using var peStream = new MemoryStream(peBytes, writable: false);
            using var peReader = new PEReader(peStream);

            // Try embedded PDB only (side-by-side .pdb files are not packaged in nupkgs).
            foreach (var entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    using var prov = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    return ReadFromPdb(prov.GetMetadataReader());
                }
            }
        }
        catch { }
        return null;
    }

    private static SourceLinkInfo? ReadFromPdb(MetadataReader reader)
    {
        var info = new SourceLinkInfo();
        // Find SourceLink CustomDebugInformation
        var sourceLinkGuid = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");

        foreach (var cdiHandle in reader.CustomDebugInformation)
        {
            var cdi = reader.GetCustomDebugInformation(cdiHandle);
            if (reader.GetGuid(cdi.Kind) != sourceLinkGuid) continue;

            var blob = reader.GetBlobBytes(cdi.Value);
            var json = Encoding.UTF8.GetString(blob);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("documents", out var docs))
                {
                    foreach (var entry in docs.EnumerateObject())
                        info.UrlMap[entry.Name] = entry.Value.GetString() ?? "";
                }
            }
            catch { }
        }

        if (info.UrlMap.Count == 0) return null;
        return info;
    }
}

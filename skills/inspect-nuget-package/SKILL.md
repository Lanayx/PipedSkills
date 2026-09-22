---
name: inspect-nuget-package
description: Inspect the public API of a .NET NuGet package
---

# Skill: NuGet Package API Inspector

Use this skill when you need to understand the public API of a .NET NuGet package: its namespaces, public types, members, signatures, XML doc summaries, and (when available) Source Link URLs to the exact source file at the build commit.

## When to use

- "What is the public API of `<PackageId>`?"
- "How do I instantiate / call / configure types in `<PackageId>`?"
- "Show me the methods / overloads on `<TypeName>` in `<PackageId>`."
- "Where is the source for `<member>` in `<PackageId>`?" (only when Source Link is embedded)
- Picking the right overload, checking nullability of parameters, finding extension methods, listing public events or operators.

Do NOT use this skill for:
- Native, analyzer-only, MSBuild-only, or content-only NuGet packages (no managed assemblies).
- Discovering implementation details of internal types (unless `--include-internal` is explicitly required).

## Requirements

- .NET 10 SDK installed and on PATH (`dotnet --list-sdks` must show a `10.x` entry).
- Network access to the NuGet feed (default: `https://api.nuget.org/v3/index.json`).

## Tool location

`inspect-nuget-package.cs` in this skill directory — a single-file C# 14 / .NET 10 file-based app (no `.csproj`). NuGet dependencies are declared inline via `#:package` directives.

Keep the adjacent `Directory.Build.props` with the script. It isolates the tool from the host repository's `Directory.Build.props`, `Directory.Build.targets`, and `Directory.Packages.props`, so repository-wide package references and Central Package Management do not interfere with the script's inline dependencies.

## How to invoke

Run from the directory containing `inspect-nuget-package.cs`. Output is JSON on stdout by default — read it directly from the process; do NOT write it to a temp file:

```bash
dotnet run inspect-nuget-package.cs -- \
  inspect <PackageId> [--version <ver-or-prefix-or-range>] [--tfm <tfm>] [--summary] \
  [--filter <regex>] [--output <file>] [--include-internal] [--max-members-per-type N] \
  [--no-source-link] [--source <feed-url>]
```

Alternatively, pass the script's absolute path to run it from another working directory. Build imports are resolved relative to the script, so changing the working directory alone does not isolate it from repository settings.

Exit codes: `0` = success, `1` = bad CLI args, `2` = unhandled exception (message on stderr).

All package processing is done in memory: the nupkg is downloaded into RAM and read with `MetadataReader`; no temp files are written for inspected package data. Only the target package is downloaded — dependencies are never fetched. (The `dotnet run` build cache for the script itself still lives under the user's home directory; that is unrelated to the inspected package.)

The first run downloads/restores the script's NuGet dependencies into a per-script build cache and is slow (~10–30s). Subsequent runs are fast.

### Recommended invocation pattern for agents

**Looking for a specific member or type? Use `--filter` — it is the fastest path and keeps output tiny.**

```bash
# Find a member by name anywhere in the package (regex, case-insensitive)
dotnet run inspect-nuget-package.cs -- inspect Oxpecker --filter TryGetHeaderValue

# Get one type in full
dotnet run inspect-nuget-package.cs -- inspect System.Text.Json --filter "^JsonSerializerOptions$"
```

Otherwise:

1. Start with `--summary` to get a small map of namespaces and types. Read it directly from stdout.
2. From the summary, identify the relevant types/namespaces.
3. Re-run with `--filter` (or without `--summary`) for member-level detail. Parse the JSON yourself, or bound the output with `--max-members-per-type`.

```bash
# Step 1: lightweight overview (~tens of KB even for large packages) — read from stdout
dotnet run inspect-nuget-package.cs -- inspect Polly --summary

# Step 2: full API for the package — agent parses the JSON directly from stdout
dotnet run inspect-nuget-package.cs -- inspect Polly
```

> **Do not write the output to `/tmp/*.json`, `~/...`, or any other on-disk path unless the user explicitly asked for a file.** The tool was designed so the agent reads results straight from the process's stdout. `--output <file>` exists only as an escape hatch for human users who want to persist a snapshot or for shells that cannot capture multi-megabyte stdout reliably; agents should not use it.

> Avoid relying on optional CLI tools like `jq` for post-processing — it is not always installed. Parse the JSON in-process, or use built-ins (`grep`, PowerShell `Select-String`, etc.) for quick text searches.

## Arguments

| Argument | Default | Purpose |
| --- | --- | --- |
| `<PackageId>` | required | NuGet package id, case-insensitive. |
| `--version <ver>` | latest stable | Exact version (`8.4.2`), prefix (`8`, `8.4` — picks highest matching, prefers stable, falls back to prerelease), or NuGet range (`[8.0,9.0)`). Without it: latest stable, falling back to latest prerelease. |
| `--tfm <tfm>` | best match | Target framework short name (e.g. `net10.0`, `net8.0`, `netstandard2.0`). If exact TFM is missing, the nearest compatible group is selected and surfaced in `targetFrameworkSubstituted`. |
| `--summary` | off | Emit only namespaces + type names + type-level XML doc summaries. ~10–50× smaller. |
| `--filter <regex>` | off | Keep only what matches. A type matches on its **full name or its simple name**; a matching type is emitted whole. Otherwise only its matching members are emitted, and types with no match are dropped. Case-insensitive. Combines with `--summary`. |
| `--output <file>` | stdout | **Agents: do not use.** Write JSON to a file. Intended for human users only; agents should read JSON from stdout and parse it in-process. |
| `--include-internal` | off | Include `internal`/`private protected`/`internal protected` types and members. Default is public + protected only. |
| `--max-members-per-type N` | 0 (unlimited) | Cap members per type to keep output bounded. |
| `--no-source-link` | off | Skip PDB/Source Link probing. Use when the package has no PDB or you do not need source URLs. |
| `--source <url>` | nuget.org v3 | Alternative feed (private feed must be open or auth-prepared by NuGet config). |

## Output schema (JSON)

Top level:

```jsonc
{
  "package": {
    "id": "...", "version": "...", "title": "...", "description": "...",
    "authors": "...", "projectUrl": "...", "repositoryUrl": "...",
    "repositoryCommit": "...", "license": "...", "tags": "..."
  },
  "chosenTargetFramework": "net10.0",
  "requestedTargetFramework": "net9.0",         // null when --tfm not supplied
  "targetFrameworkSubstituted": false,          // true if requested TFM was not present and the nearest compatible was used
  "availableTargetFrameworks": ["net8.0","net10.0","netstandard2.0"],
  "dependencies": [{ "id": "...", "range": "[1.0.0, )" }],
  "frameworkReferences": ["Microsoft.AspNetCore.App"],
  "assemblies": [
    {
      "file": "X.dll",
      "assemblyName": "X",
      "assemblyVersion": "1.2.3.0",
      "sourceLink": {
        "hasPdb": true,
        "documentMappings": [
          { "local": "/_/*", "url": "https://raw.githubusercontent.com/<org>/<repo>/<commit>/*" }
        ]
      },
      "xmlDocs": {
        "available": true,                      // an <assembly>.xml shipped next to the DLL
        "documentedMembers": 1072,
        "recoveredFromMalformedXml": false      // see "XML documentation" below
      },
      "namespaces": [
        {
          "namespace": "X.Foo",
          "types": [
            {
              "name": "X.Foo.Bar<T>",           // display name, generic parameters expanded
              "fullName": "X.Foo.Bar`1",        // metadata name, arity suffix intact
              "kind": "class|struct|interface|enum|delegate|static class",
              "isStatic": false, "isAbstract": false, "isSealed": false,
              "baseType": "...", "interfaces": ["..."],
              "genericParameters": ["T"],
              "attributes": ["System.ObsoleteAttribute"],
              "summary": "XML doc summary, normalized to single line",
              "constructors": [ /* method shape */ ],
              "methods":      [ /* method shape */ ],
              "properties":   [ /* property shape */ ],
              "fields":       [ /* field shape */ ],
              "events":       [ /* { name, handlerType, access, attributes, summary } */ ],
              "nestedTypes":  [ /* { name, kind } */ ]
            }
          ]
        }
      ]
    }
  ],
  "notes": {
    "apiSource": "ref/ | lib/",
    "extractionEngine": "System.Reflection.Metadata (MetadataReader)",
    "filter": "...",                            // echoes --filter when supplied
    "hint": "..."
  }
}
```

Method shape:

```jsonc
{
  "name": "DoStuffAsync", "access": "public",
  "isStatic": false, "isAbstract": false, "isVirtual": false, "isOverride": false,
  "isExtension": false,        // C#-style extension method ([Extension] on the method)
  "isOperator": false,         // op_Addition, op_Implicit, ...
  "returnType": "System.Threading.Tasks.Task<System.Int32>",
  "returnNullability": "notnull|nullable|oblivious|null",
  "genericParameters": ["TKey"],
  "parameters": [
    { "name": "x", "type": "System.String", "optional": false, "default": null,
      "modifier": "" /* "" | ref | out | in */, "nullability": "notnull|nullable|oblivious" }
  ],
  "attributes": ["..."],
  "summary": "XML doc summary",
  "signature": "public static System.Threading.Tasks.Task<System.Int32> DoStuffAsync<TKey>(System.String x)"
}
```

`nullability` values:
- `notnull`     — the C# 8+ nullable annotation marks this as non-nullable.
- `nullable`    — annotated as nullable (`T?`).
- `oblivious`   — assembly was compiled without `#nullable enable` (legacy, treat as unknown).
- `null` (omitted) — value type whose nullability is structural, not annotation-based (e.g. `int`).

Property shape includes `canRead`, `canWrite`, `getterAccess`, `setterAccess`, `isStatic`, `nullability`, `indexerParameters`. A property with `canWrite: true` but `setterAccess: "private"` cannot be assigned by external consumers.
Field shape includes `isStatic`, `isReadOnly`, `isConst`, `constantValue`, `nullability`. Constants are rendered as C# literals: strings quoted (`"true"`), chars quoted (`'x'`), booleans lowercase (`false`).

## Finding extension methods

Extension methods live on a static class, not on the type they extend, so they will not appear under the extended type. Two reliable approaches:

1. `--filter <MemberName>` — searches member names across every type in the package.
2. Look for methods with `"isExtension": true`; the extended type is the first entry in `parameters`.

## Source Link semantics

- `sourceLink.documentMappings[*].url` typically contains a `*` placeholder. To get the URL of a specific source file, replace `*` in the URL with the path that the local key (`local`) glob matches.
  - Example: `local = "/_/*"`, `url = ".../<commit>/*"`. A document path `"/_/src/Foo.cs"` maps to `".../<commit>/src/Foo.cs"`.
- The skill only emits the document mapping table; it does not emit per-symbol document paths. To resolve a source file URL for a specific member, fetch the repository at `package.repositoryCommit` and search there.
- Absence of `sourceLink` means the package ships no embedded PDB. Public API extraction still works fully.

## XML documentation

Summaries come from the `<assembly>.xml` shipped next to the DLL.

- Doc markup is flattened to prose: `<see cref="T:System.String"/>` renders as `System.String`, `<paramref name="x"/>` as `x`.
- Many real packages (F# ones especially) emit **malformed** XML — an unescaped `<` from prose like `Result<'T>` breaks the whole document. When strict parsing fails, summaries are recovered by scanning the file member-by-member, and `xmlDocs.recoveredFromMalformedXml` is set to `true`. A strict parser returns zero summaries for the entire package in that situation.
- Lookup is by exact XML documentation-comment ID, with a fallback to a unique name + parameter-count match. Overloads never share a summary.

## Behavior, guarantees, and limitations

- API is decoded directly from ECMA-335 metadata via `System.Reflection.Metadata.MetadataReader`. **Referenced assemblies are never resolved**, so a member whose signature mentions an unavailable type (a framework reference like `Microsoft.AspNetCore.App`, an unresolvable transitive dependency, an assembly-version mismatch) is still reported in full. Nothing is silently dropped.
- Reference assemblies under `ref/<tfm>/` are preferred over `lib/<tfm>/` when present (they are the official compile-time API surface); `notes.apiSource` reports which was used.
- Type and member visibility comes from metadata flags. Default surface is `public` + `protected` + `protected internal`.
- Operators and conversion operators (`op_Addition`, `op_Implicit`, …) are included in `methods` and flagged with `isOperator`. Property/event accessors are excluded from `methods` — they are reported on the property/event instead.
- `interfaces` lists **directly implemented** interfaces (the type's own interface-implementation rows), not the transitive closure inherited from base types.
- Only members **declared** on the type are listed; inherited members are not repeated. Follow `baseType` to see the rest.
- Type names are emitted fully qualified (`System.String`, not `string`).
- Nullability is extracted from `NullableAttribute` / `NullableContextAttribute` (C# 8+ nullable annotations). Assemblies built without `#nullable enable` report `oblivious` for every reference-type position.
- `--max-members-per-type N` is applied per category (ctors, methods, properties, fields, events, nested types) so each category gets up to N entries instead of N total.
- If a single type or member cannot be described, it is emitted with an `error` field rather than omitted.
- TFM selection precedence when `--tfm` is not provided: highest `.NETCoreApp` → `netstandard2.1` → `netstandard2.0` → highest other.
- When `--tfm` is supplied but absent from the package, the nearest compatible group is selected and the substitution is reported via `targetFrameworkSubstituted: true`.
- `CompilerGeneratedAttribute`, `NullableAttribute`, and `NullableContextAttribute` are filtered from the `attributes` list (the latter two are surfaced via the `nullability` field instead).
- This skill targets managed .NET assemblies only. Native, analyzer, build, and content packages are reported with an `error` field.

## Failure modes the agent should handle

| Symptom in JSON | Meaning | Action |
| --- | --- | --- |
| Build fails before JSON with `NU1008` (inline package versions conflict with Central Package Management) or `NU1015` (unversioned repository package references) | Host repository build settings leaked into the standalone tool | Restore the adjacent `Directory.Build.props` from this skill. Changing the working directory does not fix this; disabling Central Package Management alone also leaves injected package references behind. |
| Top-level `error: "No managed reference or lib assemblies..."` | Build/analyzer/content/native package | Stop; tell user the package has no managed public API. |
| `assemblies[*].error` | File is not a managed assembly, or its metadata is corrupt | Try a different `--tfm`; otherwise report. |
| `types[*].error` / `methods[*].error` | Single member could not be described | Use the rest of the API; for that member, fall back to repository source. |
| `xmlDocs.available: false` | Package ships no `.xml` doc file | Signatures are still complete; there are simply no summaries to show. |
| `sourceLink: null` | No embedded PDB or no Source Link blob | Use `package.repositoryUrl` + `repositoryCommit` for source navigation. |
| Expected member is absent | Almost always a wrong assumption about *where* it lives | Re-run with `--filter <MemberName>` before concluding it does not exist; extension methods live on a separate static class. |

## Quick decision tree for the agent

1. User asks about a specific member/type by name → run with `--filter <name>`.
2. User asks high-level "what's in this package?" → run with `--summary`.
3. User asks about a whole type → `--filter "^TypeName$"`.
4. User wants to read source of a specific member → check `sourceLink`. If absent, use `repositoryUrl@repositoryCommit`.
5. User asks about a different TFM (e.g., Unity, Xamarin, .NET Framework) → re-run with `--tfm`. If `targetFrameworkSubstituted: true`, tell the user no exact match existed and which TFM was actually used.
6. Output too large → re-run with `--summary`, `--filter`, or `--max-members-per-type`.
7. `--version` failed with "No version matches" → query `https://api.nuget.org/v3-flatcontainer/<id-lowercase>/index.json` for the full list of available versions, including pre-releases. You can also pass a prefix like `--version 4` to pick the highest matching version automatically.
8. User asks about parameter nullability → look at the `nullability` field on each parameter. If all values are `oblivious`, the package was not built with C# 8+ nullable annotations enabled.

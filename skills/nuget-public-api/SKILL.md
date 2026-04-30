---
name: nuget-public-api
description: Inspect the public API of a .NET NuGet package
---

# Skill: NuGet Public API Inspector

Use this skill when you need to understand the public API of a .NET NuGet package: its namespaces, public types, members, signatures, XML doc summaries, and (when available) Source Link URLs to the exact source file at the build commit.

## When to use

- "What is the public API of `<PackageId>`?"
- "How do I instantiate / call / configure types in `<PackageId>`?"
- "Show me the methods / overloads on `<TypeName>` in `<PackageId>`."
- "Where is the source for `<member>` in `<PackageId>`?" (only when Source Link is embedded)
- Picking the right overload, checking nullability of parameters, finding extension methods, listing public events.

Do NOT use this skill for:
- Native, analyzer-only, MSBuild-only, or content-only NuGet packages (no managed assemblies).
- Discovering implementation details of internal types (unless `--include-internal` is explicitly required).

## Requirements

- .NET 10 SDK installed and on PATH (`dotnet --list-sdks` must show a `10.x` entry).
- Network access to the NuGet feed (default: `https://api.nuget.org/v3/index.json`).

## Tool location

`skills/nuget-public-api/nuget-public-api.cs` — a single-file C# 14 / .NET 10 file-based app (no `.csproj`). NuGet dependencies are declared inline via `#:package` directives.

## How to invoke

Run from the repository root (paths are relative). Always prefer writing JSON to a file rather than stdout (output can be large):

```bash
dotnet run skills/nuget-public-api/nuget-public-api.cs -- \
  inspect <PackageId> [--version <ver-or-prefix-or-range>] [--tfm <tfm>] [--summary] \
  [--output <file>] [--include-internal] [--max-members-per-type N] \
  [--no-source-link] [--source <feed-url>]
```

Exit codes: `0` = success, `1` = bad CLI args, `2` = unhandled exception (message on stderr).

All package processing is done in memory: nupkgs are downloaded into RAM, assemblies are loaded via `MetadataLoadContext.LoadFromByteArray`, and no temp files are written for inspected package data. (The `dotnet run` build cache for the script itself still lives under the user's home directory; that is unrelated to the inspected package.)

The first run downloads/restores the script's NuGet dependencies into a per-script build cache and is slow (~10–30s). Subsequent runs are fast.

### Recommended invocation pattern for agents

1. Start with `--summary` to get a small map of namespaces and types.
2. Read the summary, identify the relevant types/namespaces.
3. Re-run without `--summary` (full mode) and `grep`/parse the section for those types only.

```bash
# Step 1: lightweight overview (~tens of KB even for large packages)
dotnet run skills/nuget-public-api/nuget-public-api.cs -- \
  inspect Polly --summary --output /tmp/polly-summary.json

# Step 2: full API for member-level questions
dotnet run skills/nuget-public-api/nuget-public-api.cs -- \
  inspect Polly --output /tmp/polly-full.json
```

## Arguments

| Argument | Default | Purpose |
| --- | --- | --- |
| `<PackageId>` | required | NuGet package id, case-insensitive. |
| `--version <ver>` | latest stable | Exact version (`8.4.2`), prefix (`8`, `8.4` — picks highest matching, prefers stable, falls back to prerelease), or NuGet range (`[8.0,9.0)`). Without it: latest stable, falling back to latest prerelease. |
| `--tfm <tfm>` | best match | Target framework short name (e.g. `net10.0`, `net8.0`, `netstandard2.0`). If exact TFM is missing, the nearest compatible group is selected and surfaced in `targetFrameworkSubstituted`. |
| `--summary` | off | Emit only namespaces + type names + type-level XML doc summaries. ~10–50× smaller. |
| `--output <file>` | stdout | Write JSON to a file. Strongly recommended for large packages. |
| `--include-internal` | off | Include `internal`/`private protected`/`internal protected` members. Default is public + protected only. |
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
      "namespaces": [
        {
          "namespace": "X.Foo",
          "types": [
            {
              "name": "X.Foo.Bar", "fullName": "X.Foo.Bar", "kind": "class|struct|interface|enum|delegate|static class",
              "isStatic": false, "isAbstract": false, "isSealed": false,
              "baseType": "...", "interfaces": ["..."],
              "genericParameters": ["T"],
              "attributes": ["System.ObsoleteAttribute"],
              "summary": "XML doc summary, normalized to single line",
              "constructors": [ /* DescribeMethod */ ],
              "methods":      [ /* DescribeMethod */ ],
              "properties":   [ /* DescribeProperty */ ],
              "fields":       [ /* DescribeField */ ],
              "events":       [ /* { name, handlerType, summary } */ ],
              "nestedTypes":  [ /* { name, kind } */ ]
            }
          ]
        }
      ]
    }
  ],
  "notes": {
    "apiSource": "ref/ | lib/",
    "sourceLinkAvailable": true,
    "hint": "..."
  }
}
```

`DescribeMethod` shape:

```jsonc
{
  "name": "DoStuffAsync", "access": "public",
  "isStatic": false, "isAbstract": false, "isVirtual": false, "isOverride": false,
  "returnType": "System.Threading.Tasks.Task<int>",
  "returnNullability": "notnull|nullable|oblivious|null",
  "genericParameters": ["TKey"],
  "parameters": [
    { "name": "x", "type": "System.String", "optional": false, "default": null,
      "modifier": "", "nullability": "notnull|nullable|oblivious" }
  ],
  "attributes": ["..."],
  "summary": "XML doc summary",
  "signature": "public static Task<int> DoStuffAsync<TKey>(string x)"
}
```

`nullability` values:
- `notnull`     — the C# 8+ nullable annotation marks this as non-nullable.
- `nullable`    — annotated as nullable (`T?`).
- `oblivious`   — assembly was compiled without `#nullable enable` (legacy, treat as unknown).
- `null` (omitted) — value type whose nullability is structural, not annotation-based (e.g. `int`).

`DescribeProperty` includes `canRead`, `canWrite`, `getterAccess`, `setterAccess`, `isStatic`, `indexerParameters`. A property with `canWrite: true` but `setterAccess: "private"` cannot be assigned by external consumers.
`DescribeField` includes `isStatic`, `isReadOnly`, `isConst`, `constantValue`.

## Source Link semantics

- `sourceLink.documentMappings[*].url` typically contains a `*` placeholder. To get the URL of a specific source file, replace `*` in the URL with the path that the local key (`local`) glob matches.
  - Example: `local = "/_/*"`, `url = ".../<commit>/*"`. A document path `"/_/src/Foo.cs"` maps to `".../<commit>/src/Foo.cs"`.
- The skill only emits the document mapping table; it does not currently emit per-symbol document paths. To resolve a source file URL for a specific member, you typically need the symbol's source file path which is in the PDB. If the user asks for source of a specific symbol, fetch the repository at `package.repositoryCommit` and search there, or add this as a follow-up enhancement.
- Absence of `sourceLink` means the package ships no PDB (embedded or side-by-side). Public API extraction still works fully.

## Behavior, guarantees, and limitations

- API is extracted from compiled assembly metadata (`MetadataLoadContext`). This is the authoritative public surface that consumers actually compile against.
- Reference assemblies under `ref/<tfm>/` are preferred over `lib/<tfm>/` when present (they are the official compile-time API surface).
- XML doc comments are loaded from the matching `<assembly>.xml` next to the DLL when present.
- Nullability is extracted from `NullableAttribute` / `NullableContextAttribute` (C# 8+ nullable annotations). Assemblies built without `#nullable enable` will report `oblivious` for every reference-type position.
- `--max-members-per-type N` is applied per category (ctors, methods, properties, fields, events, nested types) so each category gets up to N entries instead of N total.
- `ReflectionTypeLoadException` and missing dependencies are tolerated; affected types are emitted with an `error` field but the rest of the API is still returned.
- TFM selection precedence when `--tfm` is not provided: highest `.NETCoreApp` → `netstandard2.1` → `netstandard2.0` → highest other.
- When `--tfm` is supplied but absent from the package, the nearest compatible group is selected and the substitution is reported via `targetFrameworkSubstituted: true`.
- Dependencies are resolved best-effort to assist type loading; resolution failures are silent and non-fatal.
- `CompilerGeneratedAttribute`, `NullableAttribute`, and `NullableContextAttribute` are filtered from the `attributes` list (the latter two are surfaced via the `nullability` field instead).
- This skill targets managed .NET assemblies only. Native, analyzer, build, and content packages are reported with an `error` field.
- Package downloads are extracted with path-traversal protection: nupkg entries with absolute paths or `..` segments are skipped silently.

## Failure modes the agent should handle

| Symptom in JSON | Meaning | Action |
| --- | --- | --- |
| Top-level `error: "No managed reference or lib assemblies..."` | Build/analyzer/content/native package | Stop; tell user the package has no managed public API. |
| `assemblies[*].error` | Assembly failed to load (unresolved dep) | Re-run with a different `--tfm`, or accept partial API. |
| `types[*].error` | Single type failed (missing referenced type) | Use the rest of the API; for that type, fall back to repository source. |
| `sourceLink: null` | No PDB or no Source Link blob | Use `package.repositoryUrl` + `repositoryCommit` for source navigation. |

## Quick decision tree for the agent

1. User asks high-level "what's in this package?" → run with `--summary`.
2. User asks about a specific type or method → run full mode, then locate `assemblies[].namespaces[].types[]` by `fullName`.
3. User wants to read source of a specific member → check `sourceLink`. If absent, use `repositoryUrl@repositoryCommit`.
4. User asks about a different TFM (e.g., Unity, Xamarin, .NET Framework) → re-run with `--tfm`. If `targetFrameworkSubstituted: true` in the result, tell the user no exact match existed and which TFM was actually used.
5. Output too large → re-run with `--summary` or `--max-members-per-type`.
6. `--version` failed with "No version matches" → query `https://api.nuget.org/v3-flatcontainer/<id-lowercase>/index.json` for the full list of available versions, including pre-releases. You can also pass a prefix like `--version 4` to pick the highest matching version automatically.
7. User asks about parameter nullability → look at the `nullability` field on each parameter (`notnull`, `nullable`, `oblivious`). If all values are `oblivious`, the package was not built with C# 8+ nullable annotations enabled.

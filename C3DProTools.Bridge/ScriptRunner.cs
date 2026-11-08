using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace C3DProTools.Bridge
{
    /// <summary>
    /// Compiles an agent's C# snippet against the assemblies loaded in Civil 3D and runs it in a collectible
    /// AssemblyLoadContext, so each snippet is unloaded afterwards instead of piling up in the process.
    /// </summary>
    internal static class ScriptRunner
    {
        private const string Prefix =
            "using System;\n" +
            "using System.Linq;\n" +
            "using System.Collections.Generic;\n" +
            "using System.Text;\n" +
            "using Autodesk.AutoCAD.ApplicationServices;\n" +
            "using Autodesk.AutoCAD.DatabaseServices;\n" +
            "using Autodesk.AutoCAD.EditorInput;\n" +
            "using Autodesk.AutoCAD.Geometry;\n" +
            "using Autodesk.Civil;\n" +
            "using Autodesk.Civil.ApplicationServices;\n" +
            "using Autodesk.Civil.DatabaseServices;\n" +
            // Names that exist in both the AutoCAD and Civil namespaces.
            "using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;\n" +
            "using Surface = Autodesk.Civil.DatabaseServices.Surface;\n" +
            "using Application = Autodesk.AutoCAD.ApplicationServices.Application;\n" +
            "public static class AgentSnippet {\n" +
            "public static object Run(Document doc, Database db, Editor ed, CivilDocument civil, StringBuilder output) {\n" +
            "#line 1\n";
        private const string Suffix = "\n#line hidden\nreturn null;\n}\n}\n";

        private static List<MetadataReference> _references;
        private static readonly object Gate = new();

        public sealed class Compiled
        {
            internal byte[] Image;

            public object Run(Document doc, Database db, Editor ed, CivilDocument civil, StringBuilder output)
            {
                var alc = new AssemblyLoadContext("C3DBridgeSnippet", isCollectible: true);
                try
                {
                    Assembly asm;
                    using (var ms = new MemoryStream(Image)) asm = alc.LoadFromStream(ms);
                    var run = asm.GetType("AgentSnippet").GetMethod("Run");
                    try
                    {
                        return run.Invoke(null, new object[] { doc, db, ed, civil, output });
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        throw new SnippetException(tie.InnerException);
                    }
                }
                finally
                {
                    alc.Unload();
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Compiled Compile(string code)
        {
            var tree = CSharpSyntaxTree.ParseText(Prefix + code + Suffix,
                new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "AgentSnippet_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    platform: Platform.X64,
                    allowUnsafe: false));

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                var errors = emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(20)
                    .Select(d =>
                    {
                        var span = d.Location.GetMappedLineSpan();
                        return $"line {span.StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}";
                    });
                throw new InvalidOperationException("Snippet did not compile:\n" + string.Join("\n", errors));
            }
            return new Compiled { Image = ms.ToArray() };
        }

        private static List<MetadataReference> References()
        {
            lock (Gate)
            {
                if (_references != null) return _references;
                var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Everything Civil 3D has loaded (AutoCAD, Civil 3D, our own plugins)...
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    string loc;
                    try { loc = asm.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                    byName.TryAdd(asm.GetName().Name, loc);
                }
                // ...plus the whole .NET runtime, so System.* types resolve even if not loaded yet.
                if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                {
                    foreach (var path in tpa.Split(Path.PathSeparator))
                        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                            byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
                }

                // Civil 3D's platform list also names native DLLs (Qt, Visual LISP, ...): keep managed assemblies only.
                var refs = new List<MetadataReference>();
                foreach (var path in byName.Values)
                {
                    if (!IsManagedAssembly(path)) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
                }
                _references = refs;
                return refs;
            }
        }

        private static bool IsManagedAssembly(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                return pe.HasMetadata && System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe).IsAssembly;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>An exception thrown by the snippet itself (not by the bridge), with its own stack trace.</summary>
    internal sealed class SnippetException : Exception
    {
        public SnippetException(Exception inner)
            : base($"{inner.GetType().Name}: {inner.Message}\n{FirstFrames(inner)}", inner) { }

        private static string FirstFrames(Exception e) =>
            string.Join("\n", (e.StackTrace ?? "").Split('\n').Take(6).Select(l => l.TrimEnd()));
    }
}

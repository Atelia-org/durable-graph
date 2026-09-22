using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.SiblingGenerator;

/// <summary>
/// Probe-only sibling generator: it adds mutable instance state to Leaf in the same compiler
/// pass, simulating another source generator whose output the durable generator cannot see.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SiblingCounterGenerator : IIncrementalGenerator {
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        context.RegisterSourceOutput(context.CompilationProvider, static (context, _) =>
            context.AddSource("SiblingCounter.g.cs", SourceText.From(
                """
                namespace ImmutableLeafProbe
                {
                    public partial class Leaf
                    {
                        public int Counter { get; set; }
                    }
                }
                """, Encoding.UTF8)));
    }
}

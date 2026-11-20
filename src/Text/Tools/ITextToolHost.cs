namespace Atelia.Text;

/// <summary>A field selected by the host for the current session. Its address can be passed verbatim to either tool.</summary>
/// <param name="Address">Opaque host address identifying exactly this field, such as a memo field or a file.</param>
/// <param name="Entry">Entry label shown with search results.</param>
/// <param name="Field">Field label shown with search results.</param>
/// <param name="Editor">The live editor for this address in the current world.</param>
public sealed record TextToolTarget(string Address, string Entry, string Field, TextEditor Editor);

/// <summary>Selects the text that this session may read and edit. The tools resolve their targets on each call.</summary>
/// <remarks>
/// The host owns world and role selection, address syntax, editor lifetime and any persistence.
/// Invoke the tools serially with respect to the selected world and its editors.
/// </remarks>
public interface ITextToolHost {
    /// <summary>Enumerates the currently readable fields in the order the host wants them searched.</summary>
    IEnumerable<TextToolTarget> GetSearchTargets();

    /// <summary>Resolves one address with current read or edit permission. Return null for an unavailable address.</summary>
    TextToolTarget? Resolve(string target, bool forEdit);
}

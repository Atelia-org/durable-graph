using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Atelia.DurableGraph.SchemaHistory;

// Shared source between the compiler and history publisher. This is an open definition
// pattern, not a Runtime TypeExpr or a second persistent closed-schema identity.
internal enum PatternKind { Builtin = 1, Named = 2, Parameter = 3, VectorArray = 4, Rank2Array = 5, Rank3Array = 6, Rank4Array = 7, List = 8, Nullable = 9, Dictionary = 10 }

internal sealed class TypePattern : IEquatable<TypePattern> {
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly TypePattern[] _arguments;
    private readonly string _canonical;

    private TypePattern(PatternKind kind, int number, string? definitionId, TypePattern[] arguments) {
        Kind = kind;
        BuiltinTag = kind == PatternKind.Builtin ? number : 0;
        ParameterOrdinal = kind == PatternKind.Parameter ? number : -1;
        DefinitionId = definitionId;
        _arguments = arguments;
        Arguments = Array.AsReadOnly(arguments);
        bool containsParameter = kind == PatternKind.Parameter;
        foreach (TypePattern argument in arguments) containsParameter |= argument.ContainsParameter;
        ContainsParameter = containsParameter;
        StringBuilder text = new();
        if (kind == PatternKind.Builtin) text.Append('b').Append(number.ToString(CultureInfo.InvariantCulture));
        else if (kind == PatternKind.Parameter) text.Append('p').Append(number.ToString(CultureInfo.InvariantCulture));
        else if (IsArray) text.Append('a').Append(ArrayRank.ToString(CultureInfo.InvariantCulture)).Append('(').Append(arguments[0]._canonical).Append(')');
        else if (IsNullable) text.Append("q(").Append(arguments[0]._canonical).Append(')');
        else if (IsList) text.Append("l(").Append(arguments[0]._canonical).Append(')');
        else if (IsDictionary) text.Append("d(").Append(arguments[0]._canonical).Append(',').Append(arguments[1]._canonical).Append(')');
        else {
            text.Append('n').Append(Convert.ToBase64String(Utf8.GetBytes(definitionId!))).Append('(');
            for (int index = 0; index < arguments.Length; index++) {
                if (index != 0) text.Append(',');
                text.Append(arguments[index]._canonical);
            }
            text.Append(')');
        }
        _canonical = text.ToString();
    }

    public PatternKind Kind { get; }
    public int BuiltinTag { get; }
    public string? DefinitionId { get; }
    public IReadOnlyList<TypePattern> Arguments { get; }
    public int ParameterOrdinal { get; }
    public bool ContainsParameter { get; }
    public bool ContainsBclScalars {
        get {
            if (Kind == PatternKind.Builtin && BuiltinTag >= 19 && BuiltinTag <= 21) return true;
            foreach (TypePattern argument in _arguments) if (argument.ContainsBclScalars) return true;
            return false;
        }
    }
    public bool ContainsTemporalScalars {
        get {
            if (Kind == PatternKind.Builtin && BuiltinTag >= 22 && BuiltinTag <= 24) return true;
            foreach (TypePattern argument in _arguments) if (argument.ContainsTemporalScalars) return true;
            return false;
        }
    }
    public bool IsArray => (int)Kind >= 4 && (int)Kind <= 7;
    public bool IsList => Kind == PatternKind.List;
    public bool IsDictionary => Kind == PatternKind.Dictionary;
    public bool IsNullable => Kind == PatternKind.Nullable;
    public int ArrayRank => IsArray ? (int)Kind - 3 : 0;
    public TypePattern? ElementType => IsArray || IsList || IsNullable ? _arguments[0] : null;
    public TypePattern? KeyType => IsDictionary ? _arguments[0] : null;
    public TypePattern? ValueType => IsDictionary ? _arguments[1] : null;
    public bool ContainsDictionary {
        get {
            if (IsDictionary) return true;
            foreach (TypePattern argument in _arguments) if (argument.ContainsDictionary) return true;
            return false;
        }
    }
    public bool ContainsNullable {
        get {
            if (IsNullable) return true;
            foreach (TypePattern argument in _arguments) if (argument.ContainsNullable) return true;
            return false;
        }
    }
    public bool ContainsList {
        get {
            if (IsList) return true;
            foreach (TypePattern argument in _arguments) if (argument.ContainsList) return true;
            return false;
        }
    }
    public bool ContainsArray {
        get {
            if (IsArray) return true;
            foreach (TypePattern argument in _arguments) if (argument.ContainsArray) return true;
            return false;
        }
    }

    public static TypePattern ArrayOf(TypePattern element, int rank) {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (rank < 1 || rank > 4) throw new ArgumentOutOfRangeException(nameof(rank));
        TypePattern result = new((PatternKind)(rank + 3), 0, null, new[] { element });
        int nodes = 0;
        result.ValidateBounds(1, ref nodes);
        return result;
    }

    public static TypePattern ListOf(TypePattern element) {
        if (element is null) throw new ArgumentNullException(nameof(element));
        TypePattern result = new(PatternKind.List, 0, null, new[] { element });
        int nodes = 0;
        result.ValidateBounds(1, ref nodes);
        return result;
    }

    public static TypePattern NullableOf(TypePattern element) {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (element.IsArray || element.IsList || element.IsDictionary || element.IsNullable ||
            (element.Kind == PatternKind.Builtin && element.BuiltinTag == 4))
            throw new ArgumentException("Nullable requires a non-nullable value operand.", nameof(element));
        TypePattern result = new(PatternKind.Nullable, 0, null, new[] { element });
        int nodes = 0;
        result.ValidateBounds(1, ref nodes);
        return result;
    }

    public static TypePattern DictionaryOf(TypePattern key, TypePattern value) {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (value is null) throw new ArgumentNullException(nameof(value));
        TypePattern result = new(PatternKind.Dictionary, 0, null, new[] { key, value });
        int nodes = 0;
        result.ValidateBounds(1, ref nodes);
        return result;
    }

    public static bool IsBuiltinTag(int tag) => (tag >= 1 && tag <= 14) || (tag >= 19 && tag <= 24);

    public static TypePattern Builtin(int tag) {
        if (!IsBuiltinTag(tag)) throw new ArgumentOutOfRangeException(nameof(tag));
        return new TypePattern(PatternKind.Builtin, tag, null, Array.Empty<TypePattern>());
    }

    public static TypePattern Parameter(int ordinal) {
        if (ordinal < 0 || ordinal >= 32) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return new TypePattern(PatternKind.Parameter, ordinal, null, Array.Empty<TypePattern>());
    }

    public static TypePattern Named(string id, params TypePattern[] arguments) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A definition ID is required.", nameof(id));
        Utf8.GetByteCount(id);
        if (arguments is null || arguments.Length > 32) throw new ArgumentOutOfRangeException(nameof(arguments));
        TypePattern result = new(PatternKind.Named, 0, id, (TypePattern[])arguments.Clone());
        int nodes = 0;
        result.ValidateBounds(1, ref nodes);
        return result;
    }

    public TypePattern Substitute(IReadOnlyList<TypePattern> arguments) {
        if (Kind == PatternKind.Parameter) {
            if (ParameterOrdinal >= arguments.Count) throw new ArgumentException("Unbound type parameter.", nameof(arguments));
            return arguments[ParameterOrdinal];
        }
        if (!ContainsParameter) return this;
        TypePattern[] substituted = new TypePattern[_arguments.Length];
        for (int index = 0; index < substituted.Length; index++) substituted[index] = _arguments[index].Substitute(arguments);
        return IsArray ? ArrayOf(substituted[0], ArrayRank) : IsList ? ListOf(substituted[0]) : IsNullable ? NullableOf(substituted[0]) :
            IsDictionary ? DictionaryOf(substituted[0], substituted[1]) : Named(DefinitionId!, substituted);
    }

    public bool ParametersFit(int arity) {
        if (Kind == PatternKind.Parameter) return ParameterOrdinal < arity;
        foreach (TypePattern argument in _arguments) if (!argument.ParametersFit(arity)) return false;
        return true;
    }

    public IEnumerable<TypePattern> NamedNodes() {
        if (Kind == PatternKind.Named) yield return this;
        foreach (TypePattern argument in _arguments) foreach (TypePattern child in argument.NamedNodes()) yield return child;
    }

    private void ValidateBounds(int depth, ref int nodes) {
        if (depth > 64 || ++nodes > 4096) throw new ArgumentException("Type pattern exceeds depth 64 or 4096 nodes.");
        foreach (TypePattern argument in _arguments) {
            if (argument is null) throw new ArgumentException("A type argument cannot be null.");
            argument.ValidateBounds(depth + 1, ref nodes);
        }
    }

    public static bool TryParse(string text, int arity, out TypePattern? result, bool allowArrays = true, bool allowLists = true, bool allowNullable = true, bool allowDictionaries = true, bool allowBclScalars = true, bool allowTemporalScalars = true) {
        result = null;
        if (arity < 0 || arity > 32) return false;
        try {
            int cursor = 0, nodes = 0;
            TypePattern parsed = Parse(text, ref cursor, 1, ref nodes);
            if (cursor != text.Length || !parsed.ParametersFit(arity) || parsed.ToString() != text || (!allowArrays && parsed.ContainsArray) || (!allowLists && parsed.ContainsList) || (!allowNullable && parsed.ContainsNullable) || (!allowDictionaries && parsed.ContainsDictionary) || (!allowBclScalars && parsed.ContainsBclScalars) || (!allowTemporalScalars && parsed.ContainsTemporalScalars)) return false;
            result = parsed;
            return true;
        } catch (ArgumentException) { return false; }
          catch (FormatException) { return false; }
          catch (OverflowException) { return false; }
    }

    private static TypePattern Parse(string text, ref int cursor, int depth, ref int nodes) {
        if (cursor >= text.Length || depth > 64 || ++nodes > 4096) throw new FormatException();
        char kind = text[cursor++];
        if (kind == 'd') {
            if (cursor >= text.Length || text[cursor++] != '(') throw new FormatException();
            TypePattern key = Parse(text, ref cursor, depth + 1, ref nodes);
            if (cursor >= text.Length || text[cursor++] != ',') throw new FormatException();
            TypePattern value = Parse(text, ref cursor, depth + 1, ref nodes);
            if (cursor >= text.Length || text[cursor++] != ')') throw new FormatException();
            return DictionaryOf(key, value);
        }
        if (kind == 'l' || kind == 'q') {
            if (cursor >= text.Length || text[cursor++] != '(') throw new FormatException();
            TypePattern element = Parse(text, ref cursor, depth + 1, ref nodes);
            if (cursor >= text.Length || text[cursor++] != ')') throw new FormatException();
            return kind == 'l' ? ListOf(element) : NullableOf(element);
        }
        if (kind == 'a') {
            if (cursor + 1 >= text.Length || text[cursor] < '1' || text[cursor] > '4') throw new FormatException();
            int rank = text[cursor++] - '0';
            if (text[cursor++] != '(') throw new FormatException();
            TypePattern element = Parse(text, ref cursor, depth + 1, ref nodes);
            if (cursor >= text.Length || text[cursor++] != ')') throw new FormatException();
            return ArrayOf(element, rank);
        }
        if (kind == 'b' || kind == 'p') {
            int start = cursor;
            while (cursor < text.Length && text[cursor] >= '0' && text[cursor] <= '9') cursor++;
            if (cursor == start) throw new FormatException();
            string number = text.Substring(start, cursor - start);
            int value = int.Parse(number, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number != value.ToString(CultureInfo.InvariantCulture)) throw new FormatException();
            return kind == 'b' ? Builtin(value) : Parameter(value);
        }
        if (kind != 'n') throw new FormatException();
        int idStart = cursor;
        while (cursor < text.Length && text[cursor] != '(') cursor++;
        if (cursor == text.Length) throw new FormatException();
        string encoded = text.Substring(idStart, cursor - idStart);
        byte[] bytes = Convert.FromBase64String(encoded);
        if (Convert.ToBase64String(bytes) != encoded) throw new FormatException();
        string id = Utf8.GetString(bytes);
        cursor++;
        List<TypePattern> arguments = new();
        if (cursor < text.Length && text[cursor] != ')') {
            while (true) {
                arguments.Add(Parse(text, ref cursor, depth + 1, ref nodes));
                if (arguments.Count > 32 || cursor >= text.Length) throw new FormatException();
                if (text[cursor] != ',') break;
                cursor++;
            }
        }
        if (cursor >= text.Length || text[cursor++] != ')') throw new FormatException();
        return Named(id, arguments.ToArray());
    }

    public bool Equals(TypePattern? other) => other is not null && StringComparer.Ordinal.Equals(_canonical, other._canonical);
    public override bool Equals(object? obj) => obj is TypePattern other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_canonical);
    public override string ToString() => _canonical;
}

namespace NomadNet;

public sealed class NomadNetConfigurationForm
{
    internal const string PanicSafetyRefusal = "panic_on_interface_error is intentionally refused: native top-level interface failure terminates its owner; child-peer failure is isolated. This fixed safety ownership is not an ineffective toggle.";
    private readonly NomadNetConfigDocument _document;
    private readonly Dictionary<string, NomadNetConfigValue> _values = new(StringComparer.Ordinal);
    public string Section { get; }
    public IReadOnlyList<NomadNetConfigurationField> Fields { get; }
    public string LastText { get; private set; }

    public NomadNetConfigurationForm(string section, string text)
    {
        Fields = NomadNetConfigurationFields.ForSection(section);
        Section = section;
        _document = NomadNetConfigDocument.Parse(text, section == "reticulum" ? NomadNetEditor.MaximumBytes : NomadNetConfigDocument.MaximumBytes);
        LastText = text;
        foreach (var field in Fields) _values.Add(field.Key, NomadNetConfigurationFields.Populate(field, _document.Get(section, field.Key)));
    }

    public NomadNetConfigValue Get(string key) => _values.TryGetValue(key, out var value)
        ? value : throw new ArgumentException("Unknown section field.");

    public string Set(string key, NomadNetConfigValue value)
    {
        var field = Fields.SingleOrDefault(field => field.Key == key) ?? throw new ArgumentException("Unknown section field.");
        NomadNetConfigurationFields.Validate(field, value);
        if (field.Disposition == "safety-refused" && !Equivalent(value, field.DefaultValue))
            throw new NotSupportedException(PanicSafetyRefusal);
        if (field.Disposition == "blocked-stack" && !Equivalent(value, field.DefaultValue))
            throw new NotSupportedException("The selected stack public RNS parser does not implement " + field.Key + "; no ineffective value will be saved.");
        var previous = _values[key];
        _values[key] = value;
        try { return Render(); }
        catch { _values[key] = previous; throw; }
    }

    public string Reset(bool confirmed)
    {
        if (!confirmed) throw new ArgumentException("Reset needs explicit confirmation and still requires Save.");
        var previous = _values.ToDictionary(StringComparer.Ordinal);
        foreach (var field in Fields) _values[field.Key] = field.DefaultValue;
        try { return Render(); }
        catch
        {
            foreach (var (key, value) in previous) _values[key] = value;
            throw;
        }
    }

    public string Render()
    {
        var changes = new Dictionary<string, NomadNetConfigValue?>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var value = _values[field.Key];
            NomadNetConfigurationFields.Validate(field, value);
            var empty = value.List is { Count: 0 } || value.Scalar is "";
            if (empty) changes[field.Key] = null;
            else if (_document.Contains(Section, field.Key) || !Equivalent(value, field.DefaultValue)) changes[field.Key] = value;
        }
        var text = _document.EditSection(Section, changes);
        LastText = text;
        return text;
    }

    private static bool Equivalent(NomadNetConfigValue left, NomadNetConfigValue right) =>
        left.IsList == right.IsList && (left.List is { } list ? list.SequenceEqual(right.List!) : left.Scalar == right.Scalar);
}

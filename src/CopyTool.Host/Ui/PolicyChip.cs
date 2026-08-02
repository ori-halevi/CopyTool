using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CopyTool.Host.Ui;

/// <summary>One selectable value inside a chip.</summary>
public sealed class PolicyOption(string label, object value)
{
    public string Label { get; } = label;
    public object Value { get; } = value;
}

/// <summary>
/// A compact "name: current-value ⌄" control model.
///
/// Six policies each with up to six values would fill a window if laid out as
/// normal controls. As chips they cost one row, stay readable at a glance, and
/// every value is one click away — which is what keeps the window the size of the
/// Windows 11 copy dialog.
/// </summary>
public sealed class PolicyChip : INotifyPropertyChanged
{
    private readonly Func<object> _get;
    private readonly Action<object> _set;
    private readonly object _default;

    public PolicyChip(string name, IEnumerable<PolicyOption> options,
                      Func<object> get, Action<object> set, object defaultValue)
    {
        Name = name;
        Options = new ObservableCollection<PolicyOption>(options);
        _get = get;
        _set = set;
        _default = defaultValue;
    }

    public string Name { get; }
    public ObservableCollection<PolicyOption> Options { get; }

    public object Current => _get();

    public string CurrentLabel =>
        Options.FirstOrDefault(o => Equals(o.Value, Current))?.Label ?? "?";

    public string Text => $"{Name}: {CurrentLabel}";

    /// <summary>True when the value differs from the default, so the chip can stand out.</summary>
    public bool IsModified => !Equals(Current, _default);

    public void Select(PolicyOption option)
    {
        _set(option.Value);
        Notify(nameof(Current));
        Notify(nameof(CurrentLabel));
        Notify(nameof(Text));
        Notify(nameof(IsModified));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

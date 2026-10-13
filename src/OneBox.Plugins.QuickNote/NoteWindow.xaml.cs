using System.Windows;
using System.Windows.Controls;

namespace OneBox.Plugins.QuickNote;

/// <summary>便签窗口：文本变化即通知插件主体保存。</summary>
public partial class NoteWindow : Window
{
    private bool _loading;

    public NoteWindow() => InitializeComponent();

    /// <summary>文本变化事件（带当前全文）。</summary>
    public event Action<string>? NoteChanged;

    /// <summary>设置文本且不触发保存回调（用于回填已保存内容）。</summary>
    public string NoteText
    {
        get => NoteBox.Text;
        set
        {
            _loading = true;
            NoteBox.Text = value;
            _loading = false;
        }
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        NoteChanged?.Invoke(NoteBox.Text);
    }
}

using System.Threading.Tasks;
using System.Windows.Controls;

namespace SsdVirtual.Desktop.Pages;

/// <summary>A navigation page. Pages build their UI in code so every string comes from <see cref="Loc"/>.</summary>
public abstract class PageBase : UserControl
{
    /// <summary>(Re)creates the page content, e.g. after the language changed.</summary>
    public abstract void Rebuild();

    /// <summary>Called every couple of seconds while the page is visible.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;
}

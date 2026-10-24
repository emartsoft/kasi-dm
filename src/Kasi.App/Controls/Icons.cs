namespace Kasi.App.Controls;

/// <summary>
/// Code points of the icons the redesign uses, from the Segoe Fluent Icons font (Windows 11) and, for the same code points, Segoe MDL2 Assets
/// (Windows 10): <c>KasiIconFont</c> lists both. A glyph is a <c>TextBlock</c> with the <c>kasi-icon</c> class and one of these as its text.
/// </summary>
internal static class Icons
{
    public const string Add = "";
    public const string Cancel = "";
    public const string Settings = "";
    public const string Video = "";
    public const string Stop = "";
    public const string Search = "";
    public const string Accept = "";
    public const string Delete = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Globe = "";
    public const string Warning = "";
    public const string Calendar = "";
    public const string Sync = "";
    public const string Download = "";
    public const string Document = "";
    public const string Folder = "";
    public const string Picture = "";
    public const string Copy = "";
    public const string ChevronDown = "";
    public const string Lightning = "";
    public const string Clock = "";
    public const string List = "";
    public const string Package = "";
    public const string Music = "";
    public const string Apps = "";
    public const string Dot = "●";

    /// <summary>Every glyph, for the tests that check none is empty or repeated.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Add, Cancel, Settings, Video, Stop, Search, Accept, Delete, Play, Pause, Globe, Warning, Calendar, Sync, Download, Document, Folder,
        Picture, Copy, ChevronDown, Lightning, Clock, List, Package, Music, Apps,
    ];
}

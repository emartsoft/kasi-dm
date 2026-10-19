namespace Kasi.Engine;

/// <summary>Actions the add-download flow may offer after a duplicate check (KASI-ENG-013).</summary>
public enum DuplicateAction
{
    Resume,
    Overwrite,
    AutoRename,
}

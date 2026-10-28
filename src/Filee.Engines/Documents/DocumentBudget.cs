// What one document reader may still build: nesting (tables in tables, text boxes, notes, groups, fields) and the
// number of paragraphs and objects. Readers call it where they recurse or add to the model, so a small file that
// expands into millions of paragraphs, or nests deeply enough to overflow the stack in a writer, is refused.

namespace Filee.Engines.Documents;

/// <summary>Counts what a reader builds against <see cref="DocumentLimits"/>.</summary>
internal sealed class DocumentBudget(DocumentLimits limits)
{
    private int _depth;
    private int _paragraphs;
    private int _objects;

    public DocumentLimits Limits { get; } = limits;

    /// <summary>Current nesting depth.</summary>
    public int Depth => _depth;

    public int Paragraphs => _paragraphs;

    public int Objects => _objects;

    /// <summary>Enters a nested level (a table cell, text box, note, group); dispose the result to leave it.</summary>
    public Level Enter()
    {
        if (_depth >= Limits.MaxNestingDepth)
            throw DocumentLimits.Exceeded($"Tables, text boxes, notes or groups are nested more than {Limits.MaxNestingDepth} levels deep.");
        _depth++;
        return new Level(this);
    }

    /// <summary>Checks a count of open fields (fields inside fields).</summary>
    public void Fields(int open)
    {
        if (open > Limits.MaxNestingDepth)
            throw DocumentLimits.Exceeded($"Fields are nested more than {Limits.MaxNestingDepth} levels deep.");
    }

    /// <summary>Counts a paragraph.</summary>
    public void Paragraph()
    {
        if (++_paragraphs > Limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(Limits.MaxParagraphs)} paragraphs.");
    }

    /// <summary>Counts a table, picture, shape, text box or equation.</summary>
    public void Object()
    {
        if (++_objects > Limits.MaxObjects)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(Limits.MaxObjects)} tables, pictures and drawing objects.");
    }

    /// <summary>A nested level; leaves it when disposed.</summary>
    public readonly struct Level(DocumentBudget budget) : IDisposable
    {
        public void Dispose() => budget._depth--;
    }
}

namespace DocAssistant.Api.Modules.Ingestion.Parsing;

// The common model every parser produces and the only thing chunking sees
// (docs/decisions.md #21). Blocks are listed in reading order.
//
// PageNumber is 1-based and null when the format has no pages: a DOCX file does not
// store them (decisions #22).
public abstract record DocumentBlock(int? PageNumber);

// Level 1 is the top-level heading; deeper sections have higher numbers.
public sealed record HeadingBlock(string Text, int Level, int? PageNumber) : DocumentBlock(PageNumber);

public sealed record ParagraphBlock(string Text, int? PageNumber) : DocumentBlock(PageNumber);

// Rows[row][column] is the text of one cell. When HasHeaderRow is true, Rows[0] holds the
// column names.
public sealed record TableBlock(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasHeaderRow,
    int? PageNumber) : DocumentBlock(PageNumber);

using RagDotNet.Documents;

namespace RagDotNet.TestAssetGen;

/// <summary>
/// One block to write, independent of the output format.
/// Heading: <see cref="Level"/> and <see cref="Text"/>. Paragraph and Code: <see cref="Text"/> (code keeps its newlines).
/// List: <see cref="Items"/> as (depth, text). Table: <see cref="Rows"/>, the first row being the header.
/// </summary>
internal sealed record GenBlock(
    BlockKind Kind,
    int Level,
    string Text,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null,
    IReadOnlyList<(int Depth, string Text)>? Items = null)
{
    public static GenBlock Heading(int level, string text) => new(BlockKind.Heading, level, text);

    public static GenBlock Paragraph(string text) => new(BlockKind.Paragraph, 0, text);

    public static GenBlock Code(string text) => new(BlockKind.Code, 0, text);

    public static GenBlock List(params (int Depth, string Text)[] items) => new(BlockKind.List, 0, "", Items: items);

    public static GenBlock Table(params string[][] rows) => new(BlockKind.Table, 0, "", Rows: rows);
}

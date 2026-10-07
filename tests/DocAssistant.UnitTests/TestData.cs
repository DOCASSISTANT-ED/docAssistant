namespace DocAssistant.UnitTests;

// The shared sample documents in tests/TestData (docs/decisions.md #26). Their expected
// structure is described in tests/TestData/README.md.
internal static class TestData
{
    public const string SampleDocx = "ik-yonetmeligi.docx";
    public const string SamplePdf = "ik-yonetmeligi.pdf";

    public static FileStream Open(string fileName) =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}

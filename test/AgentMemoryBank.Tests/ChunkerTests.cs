using AgentMemoryBank.Indexing.Chunkers;
using AgentMemoryBank.Models;
using Xunit;

namespace AgentMemoryBank.Tests;

public class ChunkerTests
{
    /// <summary>
    /// The invariant everything else rests on: a chunk's body is exactly the
    /// lines it claims, and no line is claimed by two chunks. Citations are
    /// worthless otherwise, so every chunker test asserts this.
    /// </summary>
    private static void AssertFaithful(string text, IReadOnlyList<Chunk> chunks)
    {
        var lines = Chunker.SplitLines(text);
        var claimed = new HashSet<int>();

        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.LineStart, 1, lines.Count);
            Assert.InRange(chunk.LineEnd, chunk.LineStart, lines.Count);

            var expected = string.Join('\n', lines.Skip(chunk.LineStart - 1).Take(chunk.LineCount));
            Assert.Equal(expected, chunk.Body);

            for (var line = chunk.LineStart; line <= chunk.LineEnd; line++)
                Assert.True(claimed.Add(line), $"line {line} claimed by two chunks");
        }
    }

    [Theory]
    [InlineData("src/core/worker_pool.cc", "cpp")]
    [InlineData("src/Options.h", "cpp")]
    [InlineData("tools/train.py", "python")]
    [InlineData("README.md", "markdown")]
    [InlineData("configs/pipeline.textproto", "textproto")]
    [InlineData("proto/schema.proto", "proto")]
    [InlineData("configs/settings.yaml", "yaml")]
    [InlineData("scripts/setup.sh", "shell")]
    [InlineData("notes", "text")]
    public void LanguageFor_maps_the_extensions_it_supports(string path, string expected) =>
        Assert.Equal(expected, Chunker.LanguageFor(path));

    [Fact]
    public void Cpp_chunks_functions_classes_and_the_declarations_between_them()
    {
        var source = string.Join('\n', [
            "#include <string>",                    // 1
            "",                                     // 2
            "namespace acme {",                   // 3
            "",                                     // 4
            "// Fitted to real measured outcomes.",    // 5
            "struct Options {",                     // 6
            "  float retry_backoff = 1.5f;",           // 7
            "  float retry_jitter = 0.25f;",          // 8
            "};",                                   // 9
            "",                                     // 10
            "int Adder::Add(int a, int b) {",       // 11
            "  return a + b;",                      // 12
            "}",                                    // 13
            "",                                     // 14
            "}  // namespace acme",               // 15
        ]);

        var chunks = Chunker.ChunkText("cpp", source);
        AssertFaithful(source, chunks);

        var options = Assert.Single(chunks, c => c.Symbol == "Options");
        Assert.Equal("class", options.Kind);
        Assert.Equal(5, options.LineStart);      // the doc comment above is included
        Assert.Equal(9, options.LineEnd);
        Assert.Contains("retry_jitter = 0.25f", options.Body);

        var function = Assert.Single(chunks, c => c.Symbol == "Adder::Add");
        Assert.Equal("function", function.Kind);
        Assert.Equal(11, function.LineStart);
        Assert.Equal(13, function.LineEnd);

        // The namespace is transparent: nothing is chunked as just its opener or closer.
        Assert.DoesNotContain(chunks, c => c.Symbol == "acme");
    }

    [Fact]
    public void Cpp_ignores_braces_inside_strings_and_comments()
    {
        var source = string.Join('\n', [
            "void f() {",                           // 1
            "  const char* s = \"} not a brace {\";",// 2
            "  /* } still not one",                 // 3
            "     } nor this */",                   // 4
            "  g();",                               // 5
            "}",                                    // 6
            "void h() { i(); }",                    // 7
        ]);

        var chunks = Chunker.ChunkText("cpp", source);
        AssertFaithful(source, chunks);

        var f = Assert.Single(chunks, c => c.Symbol == "f");
        Assert.Equal(1, f.LineStart);
        Assert.Equal(6, f.LineEnd);
        Assert.Contains(chunks, c => c.Symbol == "h");
    }

    [Fact]
    public void Cpp_splits_an_oversized_class_into_its_members()
    {
        var fields = Enumerable.Range(0, Chunker.MaxChunkLines + 40).Select(i => $"  int field_{i} = {i};");
        var source = $"class Big {{\n{string.Join('\n', fields)}\n  void Method() {{\n    Work();\n  }}\n}};\n";

        var chunks = Chunker.ChunkText("cpp", source);
        AssertFaithful(source, chunks);

        Assert.Contains(chunks, c => c.Symbol == "Big::Method");
        Assert.All(chunks, c => Assert.True(c.LineCount <= Chunker.MaxChunkLines));
    }

    [Fact]
    public void Python_chunks_defs_decorators_and_module_level_code()
    {
        var source = string.Join('\n', [
            "import tensorflow as tf",   // 1
            "",                          // 2
            "# Fitted, not chosen.",     // 3
            "@tf.function",              // 4
            "def train_step(x):",        // 5
            "    return x + 1",          // 6
            "",                          // 7
            "class Net:",                // 8
            "    def __init__(self):",   // 9
            "        self.n = 1",        // 10
            "",                          // 11
            "CONST = 3",                 // 12
        ]);

        var chunks = Chunker.ChunkText("python", source);
        AssertFaithful(source, chunks);

        var step = Assert.Single(chunks, c => c.Symbol == "train_step");
        Assert.Equal("function", step.Kind);
        Assert.Equal(3, step.LineStart);   // decorator and comment belong to the def
        Assert.Equal(6, step.LineEnd);

        var net = Assert.Single(chunks, c => c.Symbol == "Net");
        Assert.Equal(8, net.LineStart);
        Assert.Equal(10, net.LineEnd);

        // A short module-level constant is still worth citing.
        Assert.Contains(chunks, c => c.Kind == "module" && c.Body.Contains("CONST = 3"));
    }

    [Fact]
    public void Markdown_splits_on_headings_but_not_inside_fences()
    {
        var source = string.Join('\n', [
            "intro text",       // 1
            "",                 // 2
            "## The two knobs", // 3
            "body",             // 4
            "",                 // 5
            "```bash",          // 6
            "# not a heading",  // 7
            "```",              // 8
            "",                 // 9
            "## Next",          // 10
            "more",             // 11
        ]);

        var chunks = Chunker.ChunkText("markdown", source);
        AssertFaithful(source, chunks);

        Assert.Equal([null, "The two knobs", "Next"], chunks.Select(c => c.Symbol));
        var knobs = Assert.Single(chunks, c => c.Symbol == "The two knobs");
        Assert.Equal(8, knobs.LineEnd);   // the fenced "# not a heading" stays inside
    }

    [Fact]
    public void Textproto_keeps_top_level_block_names()
    {
        var source = string.Join('\n', [
            "# loader config",      // 1
            "stage {",              // 2
            "  name: \"shuffle\"",  // 3
            "  inner { a: 1 }",     // 4
            "}",                    // 5
            "stage {",              // 6
            "  name: \"batch\"",    // 7
            "}",                    // 8
        ]);

        var chunks = Chunker.ChunkText("textproto", source);
        AssertFaithful(source, chunks);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("stage", chunks[0].Symbol);
        Assert.Equal(1, chunks[0].LineStart);   // the comment above the block is included
        Assert.Equal(5, chunks[0].LineEnd);
        Assert.Equal(6, chunks[1].LineStart);
        Assert.Equal(8, chunks[1].LineEnd);
    }

    [Fact]
    public void Yaml_chunks_one_block_per_top_level_key()
    {
        var source = string.Join('\n', [
            "name: pipeline",         // 1
            "training:",         // 2
            "  batch_size: 64",  // 3
            "  lr: 0.1",         // 4
            "model:",            // 5
            "  layers: 15",      // 6
        ]);

        var chunks = Chunker.ChunkText("yaml", source);
        AssertFaithful(source, chunks);

        Assert.Equal(["name", "training", "model"], chunks.Select(c => c.Symbol));
        Assert.Equal("training:\n  batch_size: 64\n  lr: 0.1", chunks[1].Body);
    }

    [Fact]
    public void Unknown_languages_fall_back_to_non_overlapping_windows()
    {
        var source = string.Join('\n', Enumerable.Range(0, Chunker.MaxChunkLines * 2 + 5).Select(i => $"line {i}"));

        var chunks = Chunker.ChunkText("shell", source);
        AssertFaithful(source, chunks);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.LineCount <= Chunker.MaxChunkLines));
    }

    [Fact]
    public void Empty_and_malformed_files_do_not_throw()
    {
        Assert.Empty(Chunker.ChunkText("cpp", ""));
        Assert.Empty(Chunker.ChunkText("python", "\n\n  \n"));

        var unbalanced = Chunker.ChunkText("cpp", "class Broken { void f() {");
        AssertFaithful("class Broken { void f() {", unbalanced);
    }

    [Fact]
    public void Brace_only_leftovers_are_not_indexed()
    {
        var source = "void f() {\n  g();\n}\n}\n};\n";
        var chunks = Chunker.ChunkText("cpp", source);

        Assert.Single(chunks, c => c.Symbol == "f");
        Assert.All(chunks, c => Assert.True(
            c.Symbol is not null || c.Body.Any(char.IsLetterOrDigit),
            $"contentless chunk indexed: '{c.Body}'"));
    }
}

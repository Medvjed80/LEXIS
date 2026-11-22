using System.Text.Json.Nodes;

namespace AleappReader.Core.Tests;

/// <summary>Categorize images: parsing of the model's JSON, the threshold, resuming, the store and CSV, the ONNX maths.
/// Synthetic answers only; no image leaves the test.</summary>
public sealed class ImageCategorizationTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("lexis-cat-");
    public void Dispose() { try { _dir.Delete(true); } catch (IOException) { } }

    static readonly IReadOnlyList<string> Cats = ImageCategorization.DefaultCategories;

    [Fact]
    public void A_clean_answer_is_read_with_names_matched_to_the_list()
    {
        var o = ImageCategorization.Parse("""{"categories":[{"name":"weapons","confidence":0.91},{"name":"Money/Cash","confidence":0.4}],"reason":"A pistol on a table."}""", Cats);
        Assert.Equal(CategorizationOutcome.Ok, o.Status);
        Assert.Equal([new CategoryScore("Weapons", 0.91), new CategoryScore("Money/Cash", 0.4)], o.Scores);
        Assert.Equal("A pistol on a table.", o.Reason);
    }

    [Fact]
    public void Fences_text_around_unknown_names_percentages_and_repeats_are_handled()
    {
        var o = ImageCategorization.Parse("""
            Sure! Here is the JSON:
            ```json
            {"categories":[{"name":"Drugs","confidence":85},{"name":"Spaceships","confidence":0.99},{"name":"drugs","confidence":0.5},
              {"name":"Alcohol","confidence":"70%"},{"name":"Vehicles","confidence":1.2},{"name":"Weapons"}, "Documents/IDs"],"reason":" x "}
            ```
            """, Cats);
        Assert.Equal(CategorizationOutcome.Ok, o.Status);
        var d = o.Scores.ToDictionary(s => s.Name, s => s.Confidence);
        Assert.Equal(0.85, d["Drugs"]);        // 85 read as a percentage, the better of the two kept
        Assert.Equal(0.7, d["Alcohol"]);
        Assert.Equal(1.0, d["Vehicles"]);      // clamped
        Assert.Equal(1.0, d["Weapons"]);       // no confidence given
        Assert.Equal(1.0, d["Documents/IDs"]); // a bare name
        Assert.False(d.ContainsKey("Spaceships"));
        Assert.Equal("x", o.Reason);
        Assert.Equal(o.Scores.OrderByDescending(s => s.Confidence).Select(s => s.Confidence), o.Scores.Select(s => s.Confidence));
    }

    [Theory]
    [InlineData("""{"categories":[],"reason":"refused: I cannot label this image"}""")]
    [InlineData("""{"categories":[],"reason":"x","refused":true}""")]
    [InlineData("I'm sorry, but I can't help with categorizing this image.")]
    [InlineData("I cannot assist with that.")]
    public void A_refusal_is_recorded_as_refused(string answer)
    {
        var o = ImageCategorization.Parse(answer, Cats);
        Assert.True(o.Refused);
        Assert.Equal(CategorizationOutcome.RefusedStatus, o.Status);
        Assert.Empty(o.Scores);
    }

    [Theory]
    [InlineData("")]
    [InlineData("The picture shows a beach.")]
    [InlineData("{not json")]
    public void An_answer_that_is_neither_json_nor_a_refusal_is_an_error(string answer)
    {
        var o = ImageCategorization.Parse(answer, Cats);
        Assert.Equal(CategorizationOutcome.ErrorStatus, o.Status);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public void Empty_categories_without_refusal_is_a_result_with_nothing_found()
    {
        var o = ImageCategorization.Parse("""{"categories":[],"reason":"A landscape."}""", Cats);
        Assert.Equal(CategorizationOutcome.Ok, o.Status);
        Assert.Empty(o.Scores);
    }

    [Fact]
    public void The_threshold_keeps_scores_at_or_above_it()
    {
        IReadOnlyList<CategoryScore> s = [new("Drugs", 0.6), new("Weapons", 0.59), new("Alcohol", 0.95)];
        Assert.Equal(["Alcohol", "Drugs"], ImageCategorization.Above(s, 0.6).Select(x => x.Name));
        Assert.Equal(3, ImageCategorization.Above(s, 0).Count);
        Assert.Empty(ImageCategorization.Above(s, 0.96));
        Assert.Equal(0.6, ImageCategorization.DefaultThreshold);
    }

    [Fact]
    public void The_schema_restricts_names_to_the_list_and_the_prompt_names_them()
    {
        var schema = ImageCategorization.Schema(Cats);
        var names = (JsonArray)schema["properties"]!["categories"]!["items"]!["properties"]!["name"]!["enum"]!;
        Assert.Equal(Cats, names.Select(n => (string)n!));
        var prompt = ImageCategorization.Prompt(Cats);
        Assert.All(Cats, c => Assert.Contains("- " + c, prompt));
        Assert.Contains("JSON", prompt);
    }

    [Fact]
    public void The_prompt_version_changes_with_the_list()
    {
        var a = ImageCategorization.VersionOf(Cats);
        Assert.StartsWith(ImageCategorization.PromptRevision + "-", a);
        Assert.Equal(a, ImageCategorization.VersionOf([.. Cats]));
        Assert.NotEqual(a, ImageCategorization.VersionOf([.. Cats, "Tattoos"]));
        Assert.Equal(["Drugs", "Cash", "weapons"], ImageCategorization.ParseList(" Drugs\r\nCash, weapons\n\ndrugs\nWeapons")); // first spelling kept
    }

    static MediaRef Img(string id) => new(id, id + ".jpg", "image/jpeg", "/sdcard/DCIM/" + id + ".jpg", null, null, "m", "Photos");

    [Fact]
    public void A_run_resumes_skipping_what_this_model_and_prompt_version_already_did()
    {
        var store = new ImageCategoryStore(Path.Combine(_dir.FullName, "_reader_ai.db"));
        var v1 = ImageCategorization.VersionOf(Cats);
        store.Save("a", "a.jpg", "/a.jpg", "aa", "Vision LLM (Ollama)", "qwen2.5vl:7b", v1, new([new("Drugs", 0.9)], "r"));
        store.Save("b", "b.jpg", "/b.jpg", "bb", "Vision LLM (Ollama)", "qwen2.5vl:7b", v1, new([], "refused: no", Refused: true));
        store.Save("c", "c.jpg", "/c.jpg", "cc", "Vision LLM (Ollama)", "qwen2.5vl:7b", v1, CategorizationOutcome.Failed("timeout"));
        store.Save("d", "d.jpg", "/d.jpg", "dd", "Vision LLM (Ollama)", "gemma3:4b", v1, new([new("Weapons", 0.7)], "r"));

        var done = store.Done("Vision LLM (Ollama)", "qwen2.5vl:7b", v1);
        Assert.Equal(["a", "b"], done.Order()); // an error is tried again
        var pending = ImageCategorization.Pending([Img("a"), Img("b"), Img("c"), Img("d"), Img("e"), Img("e")], done);
        Assert.Equal(["c", "d", "e"], pending.Select(m => m.RefId)); // d: another model; e once
        Assert.Empty(store.Done("Vision LLM (Ollama)", "qwen2.5vl:7b", ImageCategorization.VersionOf([.. Cats, "Tattoos"])));

        // the same image again replaces its row for that run
        store.Save("c", "c.jpg", "/c.jpg", "cc", "Vision LLM (Ollama)", "qwen2.5vl:7b", v1, new([new("Vehicles", 0.8)], "a car"));
        Assert.Contains("c", store.Done("Vision LLM (Ollama)", "qwen2.5vl:7b", v1));
        Assert.Equal(4, store.All().Count);
    }

    [Fact]
    public void The_latest_result_of_each_image_is_shown_and_an_error_does_not_hide_an_earlier_one()
    {
        var store = new ImageCategoryStore(Path.Combine(_dir.FullName, "_reader_ai.db"));
        store.Save("a", "a.jpg", "/a.jpg", "aa", "ONNX", "clip.onnx#1", "onnx-1", new([new("Drugs", 0.9)], "r"));
        Thread.Sleep(5);
        store.Save("a", "a.jpg", "/a.jpg", "aa", "ONNX", "clip.onnx#2", "onnx-1", CategorizationOutcome.Failed("boom"));
        var latest = store.Latest();
        Assert.Equal("clip.onnx#1", latest["a"].Model);
        Assert.Equal("aa", latest["a"].Sha256);
        Assert.Equal([new CategoryScore("Drugs", 0.9)], latest["a"].Scores);
    }

    [Fact]
    public void The_csv_is_labelled_ai_generated_and_neutralises_formulas()
    {
        var rows = new List<ImageCategoryResult>
        {
            new(1, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "a", "=cmd.jpg", "/sdcard/a,b.jpg", "ff", "ONNX", "m", "onnx-1",
                [new("Drugs", 0.9), new("Weapons", 0.3)], "said \"hi\"", "ok", null),
        };
        var csv = ImageCategoryStore.Csv(rows, 0.6);
        var lines = csv.Split("\r\n");
        Assert.StartsWith("# AI-generated", lines[0]);
        Assert.StartsWith("file,device_path,sha256", lines[1]);
        Assert.Contains("\"'=cmd.jpg\"", lines[2]);
        Assert.Contains("\"/sdcard/a,b.jpg\"", lines[2]);
        Assert.Contains(",Drugs,", lines[2]);              // above the threshold only
        Assert.Contains("Drugs=0.9; Weapons=0.3", lines[2]); // every score
        Assert.Contains("\"said \"\"hi\"\"\"", lines[2]);
        Assert.Contains("2023-11-14 22:13:20", lines[2]);
    }

    [Fact]
    public void Ai_tags_carry_the_prefix() => Assert.Equal("AI: Drugs", ImageCategorization.TagOf("Drugs"));

    // ---- ONNX ----

    [Fact]
    public void Softmax_and_sigmoid_give_confidences()
    {
        var p = OnnxCategorizer.Softmax([1f, 2f, 3f]);
        Assert.Equal(1f, p.Sum(), 4);
        Assert.True(p[2] > p[1] && p[1] > p[0]);
        Assert.Equal(OnnxCategorizer.Softmax([1001f, 1002f, 1003f]), p); // stable for large logits
        Assert.Equal(0.5f, OnnxCategorizer.Sigmoid(0), 5);
        Assert.True(OnnxCategorizer.Sigmoid(10) > 0.999f);

        var multi = OnnxCategorizer.Outcome([3f, -3f, 0f], ["Drugs", "Weapons", "Cash"], OnnxActivation.Sigmoid);
        Assert.Equal(["Drugs", "Cash", "Weapons"], multi.Scores.Select(s => s.Name));
        Assert.Equal(0.953, multi.Scores[0].Confidence);
        var single = OnnxCategorizer.Outcome([0f, 0f], ["A", "B"], OnnxActivation.Softmax);
        Assert.All(single.Scores, s => Assert.Equal(0.5, s.Confidence));
        Assert.Equal(CategorizationOutcome.ErrorStatus, OnnxCategorizer.Outcome([1f], ["A", "B"], OnnxActivation.None).Status);
    }

    [Fact]
    public void Pixels_are_normalised_per_channel_in_nchw_or_nhwc()
    {
        var rgb = new byte[2 * 2 * 3];
        for (var i = 0; i < 4; i++) (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (255, 0, 128);
        var o = new OnnxOptions(2, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f]);
        var t = OnnxCategorizer.Tensor(rgb, 2, o);
        Assert.Equal([1, 3, 2, 2], t.Dimensions.ToArray());
        Assert.Equal(1f, t[0, 0, 1, 1], 4);
        Assert.Equal(-1f, t[0, 1, 0, 0], 4);
        Assert.Equal((128 / 255f - 0.5f) / 0.5f, t[0, 2, 1, 0], 4);
        var h = OnnxCategorizer.Tensor(rgb, 2, o, nhwc: true);
        Assert.Equal([1, 2, 2, 3], h.Dimensions.ToArray());
        Assert.Equal(1f, h[0, 1, 1, 0], 4);
        Assert.Throws<ArgumentException>(() => OnnxCategorizer.Tensor(new byte[5], 2, o));
        Assert.Equal([0.485f, 0.456f, 0.406f], new OnnxOptions().MeanOrDefault); // ImageNet unless set
        Assert.Equal([0.229f, 0.224f, 0.225f], new OnnxOptions(Std: [0, 1, 1]).StdOrDefault);
    }

    [Fact]
    public void Labels_are_read_one_per_line_with_indexes_and_comments_dropped()
    {
        var path = Path.Combine(_dir.FullName, "labels.txt");
        File.WriteAllText(path, "# my labels\n0 Drugs\n1: Money/Cash\n2,Weapons\n\nVehicles\n");
        Assert.Equal(["Drugs", "Money/Cash", "Weapons", "Vehicles"], OnnxCategorizer.ReadLabels(path));
    }

    [Fact]
    public void A_missing_onnx_model_fails_with_its_reason()
    {
        using var c = new OnnxCategorizer(Path.Combine(_dir.FullName, "none.onnx"), Path.Combine(_dir.FullName, "none.txt"), new OnnxOptions(), (_, _) => null);
        Assert.Throws<FileNotFoundException>(c.Load);
        Assert.False(c.Remote);
        Assert.Equal("ONNX", c.Engine);
    }
}

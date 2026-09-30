using System;
using System.Collections.Generic;
using System.Linq;
using Impostor.Server.LlmBots.Llm;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class ModelNamesTests
    {
        private readonly ITestOutputHelper _output;

        public ModelNamesTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void SingleFamiliesGetTheirPlainName()
        {
            var names = ModelNames.Assign(
                new[] { "qwen/qwen3.8-27b:free", "dots-studio/dots-3-note-preview:free", "inclusionai/ling-3.0-flash-sante:free" },
                new HashSet<string>());

            Assert.Equal(new[] { "Qwen", "Dots", "Ling" }, names);
        }

        [Fact]
        public void VariantsOfOneFamilyAreToldApart()
        {
            var names = ModelNames.Assign(
                new[]
                {
                    "google/gemma-4-31b-it:free",
                    "google/gemma-4-26b-a4b-it:free",
                    "nvidia/nemotron-3-super-120b-a12b:free",
                    "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free",
                    "nvidia/nemotron-3.5-lightning:free",
                },
                new HashSet<string>());

            _output.WriteLine(string.Join(", ", names));
            Assert.Equal(new[] { "Gemma31b", "Gemma26b", "NemoSuper", "NemoNano", "NemoLightn" }, names);
        }

        [Fact]
        public void NamesAreShortUniqueAndAvoidTakenOnes()
        {
            var models = Enumerable.Repeat("google/gemma-4-31b-it:free", 4).Append("vendor/a-very-long-family-name-7b:free").ToList();
            var names = ModelNames.Assign(models, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Stanton", "Gemma" });

            _output.WriteLine(string.Join(", ", names));
            Assert.All(names, n => Assert.InRange(n.Length, 1, 10));
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain("Stanton", names);
        }
    }
}

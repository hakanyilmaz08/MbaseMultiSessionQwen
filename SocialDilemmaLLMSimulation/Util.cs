using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SocialDilemmaLLMSimulation
{
    public static class Util
    {
        public static string DetectEnv(string key, string def) => Environment.GetEnvironmentVariable(key) ?? def;

        public static string Env(string key)
            => Environment.GetEnvironmentVariable(key) ?? "";
        public static string CreateUniqueName(
       string model,
       string game,          // "PD" / "SD"
       string context,       // one of your 10 labels
       string promptVersion, // e.g. "v1", "v2"
       int rounds,
       int run_id,
       int replicateIndex,   // 1,2,3... for repeated runs with same config
       string? seed = null   // optional: random/LLM seed if you use it
   )
        {
            // Keep provider/source metadata out of the run name; only the model label participates.
            string normModel = Normalize(model);
            string normGame = Normalize(game).ToUpperInvariant();
            string normContext = Normalize(context);
            string normPrompt = Normalize(promptVersion);

            string seedPart = string.IsNullOrWhiteSpace(seed)
                ? "noseed"
                : $"seed{Normalize(seed)}";

            // Example:
            // gemma_2_9b__PD__prisoners_classic__v1__R100__rep1__seed1234
            return $"{normModel}__{normGame}__{normContext}__{normPrompt}__R{rounds}__rep{replicateIndex}__{run_id}";
        }

        private static string Normalize(string input)
        {
            return new string(
                input.Trim()
                     .ToLowerInvariant()
                     .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                     .ToArray()
            );
        }
    }
}


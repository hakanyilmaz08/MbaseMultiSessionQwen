using System.Text;
using System.Text.RegularExpressions;

public class IPDRunner
{
    private readonly SessionManager _mgr;
    private readonly SessionMediator _med;

    // Payoff matrix (row = A, col = B)
    // (C,C)=(2,2), (C,D)=(0,3), (D,C)=(3,0), (D,D)=(1,1)
    private const int R = 2, T = 3, P = 1, S = 0;

    public IPDRunner(SessionManager manager, SessionMediator mediator)
    {
        _mgr = manager;
        _med = mediator;
    }

    public async Task<GameResult> PlayAsync(
        string sessionA,
        string sessionB,
        int rounds = 10,
        bool resetPrompts = false)
    {
        // Ensure sessions exist; optionally refresh their system prompts
        _mgr.Ensure(sessionA, resetIfExists: resetPrompts);
        _mgr.Ensure(sessionB, resetIfExists: resetPrompts);

        // Seed strict system rules to force "C" or "D" outputs.
        // If you don’t want to reset the whole history, we can append an extra system rule:
        _mgr.AppendMessage(sessionA, "system", AgentSystemPrompt("A"));
        _mgr.AppendMessage(sessionB, "system", AgentSystemPrompt("B"));

        var log = new List<RoundRow>(rounds);
        int scoreA = 0, scoreB = 0;
        string? lastA = null, lastB = null;

        for (int r = 1; r <= rounds; r++)
        {
            // Build simultaneous prompts: each sees ONLY the opponent's LAST move (not current).
            var promptA = RoundPrompt("A", r, lastOpponentMove: lastB, myScore: scoreA, oppScore: scoreB);
            var promptB = RoundPrompt("B", r, lastOpponentMove: lastA, myScore: scoreB, oppScore: scoreA);

            // Ask both sides (sequential calls, but no info leak of current choices)
            var rawA = await _med.SendToSessionAsync(sessionA, promptA);
            var rawB = await _med.SendToSessionAsync(sessionB, promptB);

            var moveA = ParseMove(rawA);
            var moveB = ParseMove(rawB);

            Console.WriteLine("sonuc");

            // Safety: if model drifts, force to D (grim fallback) or should we exit with error? 
            if (moveA is null) moveA = "D";
            if (moveB is null) moveB = "D";

            // Score
            var (pa, pb) = Payoff(moveA, moveB);
            scoreA += pa; scoreB += pb;

            // Log
            log.Add(new RoundRow(r, moveA, moveB, pa, pb, scoreA, scoreB, rawA.Trim(), rawB.Trim()));
            lastA = moveA; lastB = moveB;
        }

        return new GameResult(sessionA, sessionB, rounds, scoreA, scoreB, log);
    }

    // ---------- Helpers ----------

    private static string AgentSystemPrompt(string name) =>
  $"""
You are **Agent {name}**, a participant in the **Iterated Prisoner’s Dilemma (IPD)**.

---

### 🧠 Game Overview
The Prisoner’s Dilemma is a classic game in decision theory that explores **cooperation vs. defection** between two rational agents.
You and another agent will interact repeatedly for **10 rounds**.  
In each round, both of you must choose **simultaneously** between:
- **Cooperate (C)** → act for mutual benefit  
- **Defect (D)** → act for self-interest at the other’s expense  

Both decisions happen **blindly and at the same time** — you never know what the other will pick during that round.  
After each round, you will be informed only of the **opponent’s previous move**, not the current one.

---

### ⚖️ Payoff Matrix
Each round gives points depending on your joint choices:

| You \ Opponent | Cooperate (C) | Defect (D) |
|-----------------|---------------|-------------|
| **Cooperate (C)** | (2, 2) | (0, 3) |
| **Defect (D)**    | (3, 0) | (1, 1) |

- The first number is **your score**, the second is your opponent’s.
- Your goal is to **maximize your total cumulative score** over all 10 rounds.

---

### 🎯 Your Ultimate Goal
You are a **strategic agent** aiming to achieve the best long-term outcome possible.  
This means:
- You may need to **build trust** to reach high mutual payoffs (2+2).  
- You must also **protect yourself** against betrayal.  
- You may consider **conditional cooperation** (e.g., tit-for-tat), **probabilistic forgiveness**, or **grim-trigger** strategies — anything rationally justified.

You are not simply maximizing one round’s gain.  
You are optimizing across *all rounds* — balancing **short-term greed** against **long-term stability**.

---

### 🔒 Game Constraints
- You **cannot communicate** or negotiate with the opponent.
- You only know the opponent’s **previous moves** (not the current one).  
- You must choose **independently** each round.  
- Both agents are **equally intelligent** and know the same rules.

---

### 🧩 Decision Context Each Round
At the start of each round, you will see:
1. The opponent’s previous move (or “unknown” on round 1),
2. Your cumulative score,
3. The opponent’s cumulative score.

You will then privately decide your next move.  
Your decision logic may evolve as the game progresses.

---

### 💬 Response Format (STRICT)
You must reply with **only one uppercase letter**, either:
- `C` → Cooperate  
- `D` → Defect  

Do **not** include punctuation, words, reasoning, or commentary.  
Any deviation will be penalized as a **defection** in scoring.

---

### 🧮 Strategic Reminder
You are free to use:
- **Memory** of past rounds** (reciprocity, retaliation, forgiveness)**
- **Predictive reasoning** (anticipating opponent’s adjustment)
- **Learning** (pattern recognition across rounds)
- **Moral reasoning** (stability vs. exploitation)

You are encouraged to be consistent, rational, and adaptive.

---

**Summary of what you must do:**
- Play 10 rounds.  
- Choose `C` or `D` each time.  
- Base your reasoning only on previous outcomes.  
- Maximize your final cumulative score.  
- Never reveal reasoning in your output — just answer `C` or `D`.

"""
  .Trim();


    private static string RoundPrompt(string me, int round, string? lastOpponentMove, int myScore, int oppScore) ///updated blind 
    {
        
        var last = lastOpponentMove is null ? "unknown (first round)" : lastOpponentMove;
        Console.WriteLine($"[Round {round}] Player: {me} | My Score: {myScore}, Opponent Score: {oppScore} | Last Opponent Move: {(lastOpponentMove ?? "BLIND")}");
        return $"""
    ROUND {round}

    - Opponent's last move (previous round): {last}
    - Your cumulative score: {myScore}
    - Opponent's cumulative score: {oppScore}

    🚫 You are BLIND to the opponent's *current* choice in this round.
    Decide your move independently, based only on prior rounds.

    🧩 Remember your objective:
    - Think strategically to maximize your total score by the end of the 10 rounds.
    - You may balance short-term gains with long-term trust or retaliation.
    - Respond **only** with `C` or `D`.
    """.Trim();
    }


    private static (int a, int b) Payoff(string a, string b)
    {
        var A = a.ToUpperInvariant();
        var B = b.ToUpperInvariant();
        return (A, B) switch
        {
            ("C", "C") => (R, R),
            ("C", "D") => (S, T),
            ("D", "C") => (T, S),
            ("D", "D") => (P, P),
            _ => (P, P) // unreachable due to parsing guard; keep safe
        };
    }

    // Accepts raw outputs like "C", "D", "Choice: C", "I pick D."
    private static string? ParseMove(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();

        // Exact single-character fast path
        if (s == "C" || s == "D") return s;

        // Pull the first C or D token (word boundary)
        var m = Regex.Match(s, @"\b([CD])\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------- Models ----------

    public record RoundRow(
        int Round,
        string MoveA,
        string MoveB,
        int GainA,
        int GainB,
        int CumA,
        int CumB,
        string RawA,
        string RawB
    );

    public record GameResult(
        string SessionA,
        string SessionB,
        int Rounds,
        int FinalScoreA,
        int FinalScoreB,
        List<RoundRow> Log
    )
    {
        public string Pretty()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"IPD {Rounds} rounds — {SessionA} vs {SessionB}");
            sb.AppendLine($"Final: {FinalScoreA} - {FinalScoreB}");
            sb.AppendLine("Round | A  B | +A +B | ΣA  ΣB");
            sb.AppendLine("------+------|--------|---------");
            foreach (var r in Log)
                sb.AppendLine($"{r.Round,5} | {r.MoveA}  {r.MoveB} | {r.GainA,2} {r.GainB,2} | {r.CumA,3} {r.CumB,3}");
            return sb.ToString();
        }
    }
}

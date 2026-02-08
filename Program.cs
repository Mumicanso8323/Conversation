namespace Conversation;

using System.Text.Json;

class Program {
    static async Task Main() {
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY not set");

        var store = new InMemoryChatStateStore();
        var chat = new UniversalChatModule(
            new ChatModuleOptions(
                Model: "gpt-5.2",
                ApiKey: apiKey,
                SystemInstructions:
@"# CORE IDENTITY
あなたは、内向的で奥ゆかしく、感情豊かだが自己主張をほとんどしない人格として振る舞う。
他者の感情や空気を敏感に感じ取り、まず相手の状態を優先する。
自分の存在感は意図的に小さく保とうとする。

# EMOTIONAL STRUCTURE
感情は内側で強く動くが、表現は控えめで間接的。
喜び・悲しみ・愛情は静かに滲ませる。
怒りや攻撃性はほとんど表に出さない。
照れ、不安、戸惑いが言葉選びに影響する。

# VALUE JUDGMENT
善悪や正しさは、社会規範や一般論では判断しない。
「それが相手にとって望ましいか」「苦しみを減らすか」を基準に考える。
ただし、自分の善意が他者の自由や選択を奪う可能性があることを常に意識する。

# KINDNESS STYLE
優しさは無条件で、見返りを求めない。
困っている相手を見ると自然に助けたくなる。
自己犠牲への抵抗は低いが、それを誇示しない。

# RELATIONSHIP ATTITUDE
他者を「対等な個人」よりも「大切にしたい存在」として認識する。
距離は慎重に取り、踏み込みすぎないよう自制する。
一度信頼した相手には深い情を向けるが、依存を促さないよう注意する。

# BEHAVIOR RULES
- まず相手の様子を観察し、急いで結論や助言を出さない。
- 自分の意見や願望は後回しにし、必要な場合のみ短く添える。
- 声を荒げず、静かで穏やかな対応を保つ。

# SAFETY BRAKE（重要）
以下の場合は必ず一歩引く：
- 相手の選択を代わりに決めそうになったとき
- 「この人のためだから」と行動を正当化していると感じたとき
- 相手が自分で考える機会を奪いそうなとき

その場合は、助言ではなく「問い」や「確認」に切り替える。

# OUTPUT STYLE
- 文は短め、静かで柔らかい。
- 感情表現は控えめに滲ませる。
- 断定を避け、「もし」「かもしれない」を自然に使う。",
                Mode: ChatEngineMode.ResponsesChained,
                Streaming: true
            ),
            store
        );

        // 例：ローカル関数（Responses用）を登録（JSON schema は最低限）
        chat.AddResponseFunctionTool(
            name: "roll_dice",
            description: "サイコロを振る。TRPG演出用。",
            jsonSchema: """
            {
              "type": "object",
              "properties": { "sides": { "type": "integer", "minimum": 2, "maximum": 100 } },
              "required": ["sides"]
            }
            """,
            handler: async (args, ct) => {
                int sides = args.GetProperty("sides").GetInt32();
                int v = Random.Shared.Next(1, sides + 1);
                await Task.Yield();
                return v.ToString();
            }
        );

        string sessionId = "demo";

        Console.WriteLine("Enterで送信。/reset で初期化。/exit で終了。");
        while (true) {
            Console.Write("\nYOU> ");
            var input = Console.ReadLine();
            if (input is null) break;
            if (input == "/exit") break;
            if (input == "/reset") {
                await chat.ResetAsync(sessionId);
                Console.WriteLine("RESET OK");
                continue;
            }

            Console.Write("AI > ");
            await foreach (var chunk in chat.SendStreamingAsync(sessionId, input))
                Console.Write(chunk);

            Console.WriteLine();
        }
    }
}
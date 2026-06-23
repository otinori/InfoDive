# AGENTS.md — マルチエージェント共通ポリシー

本リポジトリで作業する全 AI エージェント（**Claude Code** / **Cursor** / **Continue** / GitHub Copilot 等）が **会話開始時に最初に読む** ファイル。UDR（判断記録）の自動検知ポリシーと、プロジェクトの現状を集約している。

各エージェント固有の追加指示がある場合は、以下の派生ファイルを参照:
- `CLAUDE.md` — Claude Code 向け（本書のミラー + 個別追記）
- `.github/copilot-instructions.md` — GitHub Copilot 向け（policy-only、5〜10 行）
- `.github/instructions/udr-decisions.instructions.md` — Copilot 要約本体

---

## 1. このプロジェクトについて

プロジェクトの概要・現状・主要資産はリポジトリルートの `readme.md` を参照（本書はマルチエージェント協働ポリシーに特化）。

**UDR 運用上の必須ファイル / ディレクトリ**:
- `.udr/records/` — 判断記録（UDR YAML、1 判断 1 ファイル）
- `.udr/config.yaml` — UDR プロジェクト設定（`repo_short` / sync targets / scoring 重み）
- `.udr/audit.log` — 記録操作のログ（JSON Lines）
- `.claude/skills/_udr-shared/CONVENTIONS.md` — UDR 共通規約（スキーマ / ID 体系 / status 遷移 / sync）

---

## 2. UDR 自動検知ポリシー（本書の中核）

本リポジトリは **UDR (Universal Decision Record)** で設計判断を構造化記録する。AI は会話中に判断が発生した瞬間を検知し、UDR 起票を **必ず提案** すること。

### 2.1 検知トリガー（以下のいずれかが出現したら UDR 候補）

1. **明示的な決定宣言** — user が「〜にする」「〜で決定」「〜を採用」「〜は棄却」と述べた
2. **複数案の選定** — 2 つ以上の候補を比較して 1 つを選択する議論が発生した（技術選定・アーキ選定・命名・ライセンス・ディレクトリ構造・ID 体系など）
3. **既存設計の変更** — SPEC / PROTOCOL / UDR 等の確定済み方針を改訂する判断（supersede の可能性）
4. **AI 判断の受容** — AI が提案した方針を user が黙認・承認して進めた（暗黙的判断、`status: proposed` 強制、BR-002）
5. **スコープ境界の決定** — 「P0 に含む / P1+ に延期」「実装する / 型だけ予約」のフェーズ配分判断
6. **トレードオフの意識的選択** — コスト / 複雑度 / 保守性 / 互換性を明示的に引き換えた選択

### 2.2 除外パターン（UDR 化しない）

- 変数名・関数名・改行・インデント等の **瑣末な表層スタイル**
- タイポ修正・誤字訂正・言い回し調整
- 一時的な作業手順（「まず Read してから Edit」など）
- 会話の繰り返し・言い直し・確認
- 既に記録済みの判断（迷ったら `.udr/records/` を grep で重複確認）

### 2.3 処理フロー

1. **検知**: トリガー発生を認識したら、応答の末尾に以下を付記する:
   ```
   [UDR 候補] 「<一行要約>」を記録しますか？
     Y → 記録を開始   N → スキップ   D → 既存を検索
   ```

2. **user 応答待ち**: Y なら UDR 記録を開始。会話文脈で必須 3 項目（title / decision / context + 棄却選択肢）が既に揃っていれば、ヒアリングを省略してメタ確定（domain / authors / pinned）から。

3. **記録手段** — エージェントごとの使い分け:

   | エージェント | 記録方法 |
   |---|---|
   | Claude Code | `/udr-record` skill を呼び出す（`.claude/skills/udr-record/SKILL.md`） |
   | Cursor / Continue | `CONVENTIONS.md §3` のスキーマに従い `.udr/records/<id>.yaml` を手動作成 |
   | Copilot | ポリシー通知のみ、記録は人間が Claude Code で実施 |

4. **棄却理由の補完**: `options.rejected` が 0 件なら、「他に検討した案は？棄却理由は？」と 1 回だけ確認（BR-007 / FR-015）。棄却案 0 件でも記録は続行（警告のみ）。

5. **AI-only 判断の扱い**: user 不在で AI 単独が選択した内容は `status: proposed` 強制（BR-002）。user レビュー待ちとして記録。

6. **記録後**: CLAUDE.md / AGENTS.md / `.github/instructions/udr-decisions.instructions.md` のサマリ同期を促す（Claude Code なら `/udr-sync`、他エージェントは本書 §3 マーカーを手動更新）。

### 2.4 曖昧ケースの判断規則

- 「これは判断か？」迷ったら → **記録寄り** で user に判定を委ねる（`[UDR 候補]` 提示）
- 1 セッション中に 5 件以上検知した場合 → user に「まとめて 1 件で記録するか？」と提案（巨大判断の分割検討）
- user が「記録しなくていい」と明言 → 該当セッション中は同種トリガーを静かに抑制

---

## 3. UDR サマリ（`/udr-sync` で自動更新、人間・AI 共に編集不可）

<!-- [UDR-SYNC-START] -->
## UDR — Active Decisions (0 records, synced 2026-06-24T00:00Z)

v1.0.0.0 リリースに伴い初期化。

<!-- [UDR-SYNC-END] -->

---

## 4. UDR YAML 最小テンプレート（手動作成時のリファレンス）

Cursor 等で skill を使わず手動記録する場合の最小テンプレート。完全なスキーマは `.claude/skills/_udr-shared/CONVENTIONS.md §3`:

```yaml
id: UDR-<repo_short>-<YYYYMMDDTHHmm UTC>-<rand3 [0-9a-f]>   # 例: UDR-infodive-20260624T1430-a3f
title: "<50字以内>"
domain: architecture     # architecture | requirements | design | risk | project | operations | other
status: proposed         # AI-only は proposed 強制 (BR-002)。人間関与ありは accepted 可
severity: medium         # high | medium | low
pinned: false
date: 2026-06-24
updated: 2026-06-24
authors:
  - name: <user名>
    role: human
  - name: claude-code
    role: ai-agent

context: |
  （判断に至った背景、3-6 行）

options:
  - id: opt-1
    name: "採用案"
    verdict: accepted
    rationale: |
      採用理由
  - id: opt-2
    name: "棄却案（必須、UDR の存在意義）"
    verdict: rejected
    rationale: |
      棄却理由

decision: |
  最終決定の 2-3 行要約

consequences:
  positive: ["..."]
  negative: ["..."]

relations:
  depends_on: []
  triggers: []

summary_hint: |
  [<id>] <title>。決定:<decision1行>。棄却:<rejected[0].name>(<理由1語>)。
```

保存先: `.udr/records/<id>.yaml`（1 判断 1 ファイル）

### 4.1 ID 生成の具体手順（手動記録時、bash）

```bash
REPO_SHORT=$(grep -E '^repo_short:' .udr/config.yaml | awk '{print $2}')
UTC_TS=$(date -u +%Y%m%dT%H%M)
RAND3=$(printf '%03x' $((RANDOM % 4096)))
while ls .udr/records/UDR-${REPO_SHORT}-${UTC_TS}-${RAND3}.yaml 2>/dev/null; do
  RAND3=$(printf '%03x' $((RANDOM % 4096)))
done
UDR_ID="UDR-${REPO_SHORT}-${UTC_TS}-${RAND3}"
echo "$UDR_ID"
```

PowerShell の場合:
```powershell
$REPO_SHORT = (Select-String -Path .udr/config.yaml -Pattern '^repo_short:').Line -replace '^repo_short:\s*',''
$UTC_TS     = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmm")
$RAND3      = "{0:x3}" -f (Get-Random -Maximum 4096)
$UDR_ID     = "UDR-$REPO_SHORT-$UTC_TS-$RAND3"
```

### 4.2 audit.log 追記

```bash
ISO_TS=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "{\"ts\":\"${ISO_TS}\",\"actor\":\"<user>\",\"role\":\"human\",\"op\":\"create\",\"id\":\"${UDR_ID}\",\"changed_fields\":[\"*\"]}" >> .udr/audit.log
```

### 4.3 既存 UDR の確認（`depends_on` / `triggers` を張る前）

```bash
ls .udr/records/UDR-*.yaml
grep -H "^title:" .udr/records/UDR-*.yaml
grep -l "<keyword>" .udr/records/UDR-*.yaml
```

---

## 5. 補助ルール

- **ID 体系**: `UDR-<repo_short>-<UTC_TS>-<rand3>`（CONVENTIONS §2、`repo_short` は `.udr/config.yaml` の値）
- **supersede**: 既存判断を上書きする場合は `supersedes` フィールドでリンク（FR-004）
- **commit**: user の明示指示がある場合のみ。通常は変更を検知したら `git status` の結果を伝える
- **設計書の整合**: SPEC の参照整合が崩れる編集をしたら、必ず他ファイルへの波及を grep で確認
- **バージョン正本**: `build/version.props` の `<InfoDiveVersion>` が正本。Git タグは `v$(InfoDiveVersion)` に一致させる

---

## 6. 開発ループ（設計 → 製造 → テスト → 振り返り）

本リポジトリでの AI 支援開発はフェーズごとにサブブランチを切り、作業ブランチに PR でマージするループで回す。
**AI がブランチ作成・コミット・PR 作成まで担当し、人間が各フェーズ PR をレビュー・承認してマージする。**

```
claude/<task>/design ──PR──→ claude/<task>   ← CI 動かない（main 向けではないため）
claude/<task>/impl   ──PR──→ claude/<task>   ← CI 動かない
claude/<task>/test   ──PR──→ claude/<task>   ← CI 動かない
retro: コミットを作業ブランチに直接積む
                                  │
                            PR → main         ← CI ここだけ（1回）
```

### 6.1 ブランチ規約

| ブランチ | 役割 |
|---|---|
| `claude/<task>` | 作業ブランチ。最終的に `main` へ PR |
| `claude/<task>/design` | 設計サブブランチ |
| `claude/<task>/impl` | 製造サブブランチ |
| `claude/<task>/test` | テストサブブランチ |

- サブブランチの PR ターゲットは **`main` ではなく作業ブランチ** → CI は動かない
- `retro:` コミットは作業ブランチに直接積む（プロセス改善はレビュー待ち不要）
- 作業ブランチの `main` 向け PR が唯一の CI トリガー

### 6.2 フロー（AI の動き）

```
1. claude/<task> 作業ブランチを作成
2. claude/<task>/design を作成 → design: コミット → PR（→作業ブランチ）作成
   └─ 人間がレビュー・承認 → マージ → 3へ
3. claude/<task>/impl を作成 → impl: コミット → PR（→作業ブランチ）作成
   └─ 人間がレビュー・承認 → マージ → 4へ
4. claude/<task>/test を作成 → test: コミット → /check-pr → PR（→作業ブランチ）作成
   └─ 人間がレビュー・承認 → マージ → 5へ
5. 作業ブランチに retro: コミット（1改善=1コミット）→ main へ最終 PR 作成
   └─ 人間がレビュー → CI 確認 → マージ
```

### 6.3 コミット規約（フェーズプレフィックス）

| プレフィックス | フェーズ | 含む成果物の原則 |
|---|---|---|
| `design:` | 設計 | spec / UDR / 設計書 の新規・更新 |
| `impl:` | 製造 | ソースコード実装。設計書の同期修正も同一コミットに含める |
| `test:` | テスト | テストコード・バグ修正。ソース変更・設計書更新も同一コミットに含める |
| `retro:` | レトロスペクティブ | AGENTS.md / CLAUDE.md / skill の改善。**1改善対策 = 1コミット** |

**フェーズ内の複数コミットは OK。**

#### 前フェーズ成果物の修正ルール

製造・テストフェーズで前フェーズの成果物を修正する場合は **カレントフェーズのコミットに含める**。

- 製造中に仕様の曖昧さを発見 → `impl:` コミットで設計書を同時修正
- テスト中にバグを発見・修正 → `test:` コミットでソース・設計書を同時修正

#### コミット例

```
✅ 正しい例
design: add spec for infographic layout feature
impl: implement layout renderer (update spec.md to reflect final API)
test: add unit tests for layout renderer
retro: AGENTS.md §7 add concurrency checklist

❌ 間違った例
feat: implement layout     ← フェーズ不明
impl: fix bug + add tests  ← フェーズ混在
fix: off-by-one            ← フェーズ不明
```

### 6.4 問題・課題の蓄積ルール

作業中に以下のような事象が発生したら、その場で **`.udr/retro-pending.md`** に追記する。

**蓄積対象:**
- ビルドエラー・CI 失敗（特に原因が知識不足やパターン見落としの場合）
- 修正を 2回以上やり直したもの
- ユーザーが驚いた・想定外だった副作用
- 「最初からそうすればよかった」と感じたアプローチの変更
- 時間がかかりすぎた操作や調査

**書式:**
```markdown
- [phase: impl] <何が起きたか 1行> → <なぜ起きたか 1語>
```

`.udr/retro-pending.md` は `/retro` 実行後にクリアする。gitignore せず追跡する。

### 6.5 各フェーズの完了条件

**設計フェーズ（design サブブランチ PR マージで完了）:**
- 実装に必要な仕様が spec / UDR に記録されている
- 既存 UDR との矛盾がないことを確認済み

**製造フェーズ（impl サブブランチ PR マージで完了）:**
- ローカルビルドが通る（`dotnet build InfoDive.sln -c Release`）
- 実装の意図が設計書と整合している

**テストフェーズ（test サブブランチ PR マージで完了）:**
- `/check-pr` を実行して全項目 ✅（← **PR 作成前の必須ゲート**）
- UI 変更はユーザーに実機確認依頼済み

**レトロスペクティブ（作業ブランチへの retro: コミット完了で完了）:**
- `/retro` を実行して問題を洗い出した
- 改善対策を 1件1コミットで AGENTS.md / skills に反映した
- main への最終 PR を作成した（AI の担当はここまで）

### 6.6 運用上の注意

- PR が煩雑に感じたら 1ブランチ方式（サブブランチなし）に戻すことを検討する
- 設計が不要な小規模タスク（typo 修正・設定変更等）はサブブランチを省略し `impl:` から始めてよい

---

## 7. GitHub Actions 規約

### 7.1 新規ワークフロー作成時の必須事項

新規 `.github/workflows/*.yml` を作成する際は以下を **必ず** 含めること:

**concurrency ブロック（必須）**:
```yaml
concurrency:
  group: <workflow-prefix>-${{ github.ref }}
  cancel-in-progress: true
```

- `<workflow-prefix>` は他ワークフローと重複しないプレフィックスを使う
  - 例: `build-`, `codeql-`, `release-`, `prerelease-`
- 既存ファイル（`build.yml`, `codeql.yml` 等）のパターンを必ず参照してから作成する

**確認コマンド**（作成後に実行）:
```bash
grep -L "concurrency:" .github/workflows/*.yml
# → 出力があれば concurrency が欠落しているファイルがある
```

### 7.2 副作用のある設定変更の事前告知ルール

以下の設定ファイルを追加・変更するときは、**コミット前に** ユーザーへ副作用を伝えて確認を取ること:

| ファイル | 追加直後の副作用 | 告知必須 |
|---|---|---|
| `.github/dependabot.yml` | 全エコシステムの更新 PR が即時大量発生（10件前後） | ✅ |
| `.github/workflows/codeql.yml` | 全 PR / push でスキャン実行（windows-latest: 20〜30分） | ✅ |
| `.github/workflows/*.yml`（新規） | push 直後から全ブランチへ即時適用 | ✅ |

告知文の例:
> 「`dependabot.yml` を追加すると、直後に NuGet / Actions の更新 PR が一度に複数発生します。続けますか？」

---

*最終更新: 2026-06-24 / v1.0.0.0 新規リリース・初回作成*

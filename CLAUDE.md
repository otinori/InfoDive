# CLAUDE.md — Claude Code 向け作業ガイド

**最初に `AGENTS.md` を必ず読むこと。** 本リポジトリの UDR 自動検知ポリシー、プロジェクト構成、手動テンプレート等のマスタは `AGENTS.md` に集約されている。本書は Claude Code 固有の追加指示のみを扱う。

---

## Claude Code 固有の運用

### skill の使い方

UDR 運用 skill 群は `.claude/skills/` に配置済み。AGENTS.md §2.3 の検知フローに従って候補を検知したら、以下の slash コマンドで記録・管理する:

| コマンド | 用途 |
|---|---|
| `/udr-init` | `.udr/` 構造を初期化（既に初期化済み、通常は不要） |
| `/udr-record` | 対話フローで UDR を新規記録。AGENTS.md §2.1 の検知トリガー発生時に呼ぶ |
| `/udr-search <keyword>` | ID 直接 / フィルタ / 全文検索で既存 UDR を検索 |
| `/udr-sync` | CLAUDE.md / AGENTS.md 等のサマリ同期。pinned 最大 5 件 + auto 最大 15 件 |
| `/udr-trace <id> [--impact]` | DAG 上下流の追跡。変更影響分析 |
| `/udr-review` | 品質棚卸し（orphan / AI 承認待ち / 棄却理由欠落検出） |
| `/check-pr` | PR 作成前チェック（branch / concurrency / 副作用告知 / version 確認）。製造→テスト移行ゲート |
| `/retro` | 振り返り対話フロー。問題をカテゴリ分類し AGENTS.md / skills へ改善を反映 |

**[UDR 候補] 提示時の action ボタン**: `AGENTS.md §2.3` の処理フローで応答末尾に付記する `Y/N/D` はそれぞれ `/udr-record` / スキップ / `/udr-search` に対応。

### 実装作業時のスタイル

実装タスクに入ったら以下を守ること:

**禁止:**
- 長文解説・ベストプラクティス講義・余談
- 将来改善案・複数案の並列提示

**要求:**
- まず結論、まずコード
- 必要最低限の説明のみ
- 不明点は質問 1 個だけ

**出力形式:**
```
RESULT:
（成果物）

NEXT:
（確認が必要な場合のみ）
```

---

## UDR サマリ（`/udr-sync` で自動更新、編集不可）

<!-- [UDR-SYNC-START] -->
## UDR — Active Decisions (0 records, synced 2026-06-24T00:00Z)

v1.0.0.0 リリースに伴い初期化。

<!-- [UDR-SYNC-END] -->

---

*最終更新: 2026-06-24 / 実装スタイル追加・Requirements_prompts.md を統合*

# GitHub Copilot Instructions

**policy-only** モード（CONVENTIONS §5.1 準拠）。行動指示のみを記載し、判断要約本体は `.github/instructions/udr-decisions.instructions.md` へ同期される。

- 本リポジトリの設計判断は **UDR (Universal Decision Record)** で構造化記録する。詳細ポリシー・検知トリガー・YAML テンプレートは `AGENTS.md` 参照
- コード補完・提案がアーキ判断 / 技術選定 / 仕様変更 / トレードオフ選択に該当する場合、user に「UDR として記録するか」を確認する（AGENTS.md §2.1 の 6 トリガーに従う）
- 記録は `.udr/records/<id>.yaml` に配置。ID 体系は `UDR-<repo_short>-<UTC_TS>-<rand3>`（`repo_short` は `.udr/config.yaml`、CONVENTIONS §2）
- 既存判断の上書きは `supersedes` フィールドでリンクし、旧判断の status は `superseded` に更新（FR-004）
- 判断要約は `/udr-sync`（Claude Code 経由）で自動同期。Copilot 単独では要約更新を行わない

<!-- [UDR-SYNC-START] -->
## UDR — 判断記録ポリシー (synced 2026-06-24T00:00Z)

このプロジェクトは判断記録に UDR を使用しています。

- 判断時は `/udr-record` で記録する (棄却理由の併記が必須)
- コード変更前に `/udr-search` で関連判断を確認する
- 矛盾しそうな変更は `/udr-trace <id>` で影響分析する
- アクティブ判断の要約: `.github/instructions/udr-decisions.instructions.md`
<!-- [UDR-SYNC-END] -->

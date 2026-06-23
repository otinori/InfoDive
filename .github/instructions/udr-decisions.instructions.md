---
applyTo: "**/*"
---

# UDR Decisions — 判断記録サマリ

本ファイルは GitHub Copilot（および他エージェント）が本リポジトリ内の任意ファイル編集時に参照する **判断要約**。`/udr-sync` により自動生成される（手動編集不可）。

詳細ポリシーは `AGENTS.md`、完全な判断本体は `.udr/records/<id>.yaml` 参照。

---

## 記録 / 参照の原則

- 新規判断が発生したら、`AGENTS.md §2.3` の処理フローに従い UDR として起票
- 既存コードを変更する際は、該当領域の UDR（`domain: design/architecture/risk` 等）を `.udr/records/` で確認し、その決定を尊重する
- UDR と矛盾する実装を提案する場合、必ず user に「既存 UDR を supersede するか、既存 UDR に従うか」を確認（FR-004）
- `status: proposed` の UDR は未承認。実装の根拠として使わず、user の承認を促す

---

## 現行判断サマリ（`/udr-sync` 自動生成、編集不可）

<!-- [UDR-SYNC-START] -->
## UDR — Active Decisions (0 records, synced 2026-06-24T00:00Z)

v1.0.0.0 リリースに伴い初期化。

<!-- [UDR-SYNC-END] -->

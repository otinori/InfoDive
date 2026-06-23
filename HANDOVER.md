# HANDOVER — InfoDive

> AI がセッション開始時に最初に読むファイル。常に最新状態を保つ。
> **最初に `AGENTS.md` を読んでから作業を開始すること。**

## 現在地

- **バージョン**: 1.0.0.0
- **ブランチ**: `main`
- **最終作業**: ドキュメント・ビルド構成を整備。v1.0.0.0 として新規リリース準備完了。
  - `Directory.Build.props`（ルート）→ `build/Directory.Build.props` → `build/version.props` の 3 層構成確立
  - `InfoDive.csproj` のバージョン直書きを削除し `version.props` へ一元化
  - SPEC-0001 を Electron 計画から WPF 実装に全面改訂

## 次にやること

- [ ] GitHub リポジトリへ push し、`v1.0.0.0` タグを打って Release ワークフローを動かす
- [ ] テストプロジェクト `tests/apps/InfoDive.Tests/` の実装（xUnit）
- [ ] `src/installer/wix/InfoDive.wxs` のファイルパス参照を新構成に合わせて修正・動作確認
- [ ] CodeQL / Dependabot が有効化されることをリポジトリ設定で確認

## 未解決事項・注意点

- `src/installer/wix/InfoDive.wxs` のファイルパス参照が旧構成のままの可能性あり。ローカルビルドで確認すること。
- `.github/dependabot.yml` を push すると NuGet / Actions の更新 PR が即時大量発生する（AGENTS.md §7.2 参照）。

## 構成概要

```
src/apps/InfoDive/   ← WPF アプリ本体
src/installer/wix/   ← WiX インストーラー定義
tools/               ← ビルド・発行・アイコン生成スクリプト
docs/design/spec/    ← 仕様書 (SPEC-xxxx-*.md)
docs/manual/         ← 利用者向けドキュメント
reference/           ← 参照用プロトタイプ (prezi_tool.tsx)
version.props        ← バージョン番号の正本（InfoDiveVersion）
tests/               ← テストプロジェクト
.udr/                ← UDR 判断記録
.claude/skills/      ← Claude Code スキル (check-pr, retro)
```

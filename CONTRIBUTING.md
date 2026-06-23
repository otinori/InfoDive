# Contributing — InfoDive

開発者向けガイド。

## 前提 SDK

- .NET 9.0 SDK
- Visual Studio 2022 17.x 以上 (または Rider)
- PowerShell 7+ (`pwsh`)
- WiX Toolset v5 (インストーラービルド時)

## クイックビルド

```powershell
pwsh tools/build.ps1
```

## セッション開始時

1. `HANDOVER.md` を読む
2. `tasks/TASKS.md` でアクティブタスクを確認
3. `CLAUDE.md` でリポジトリ作法を確認

## ブランチ規約

| プレフィックス | 用途 |
|---|---|
| `feat/` | 新機能 |
| `fix/` | バグ修正 |
| `docs/` | ドキュメントのみ |
| `refactor/` | リファクタリング |

## コミット規約

```
<type>(<scope>): <summary>

例:
feat(MainWindow): キーフレームのドラッグ並べ替えを追加
fix(installer): WiX パスを新構成に対応
```

## PR ルール

- `main` へ直接プッシュしない
- PR タイトルはコミット規約に従う
- CI (`build.yml`) が通ることを確認してからマージ

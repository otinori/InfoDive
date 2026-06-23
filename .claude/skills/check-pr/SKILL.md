# /check-pr — PR 作成前チェックリスト

PR を作成する前に実行し、機械的なミスを事前に検出する。
テストフェーズ（test サブブランチ）の PR 作成前に必ず実行する。

---

## 実行手順

### Step 1: 現在のブランチと PR ターゲットを確認

```bash
git branch --show-current
```

- サブブランチ（`claude/<task>/design` 等）の場合: PR ターゲットは作業ブランチ `claude/<task>`（**main ではない**）
- 作業ブランチ（`claude/<task>`）の場合: PR ターゲットは `main`

`main` を誤ってターゲットにしていないか確認する。

### Step 2: コミット構成の確認

**サブブランチの場合:**
```bash
git log --oneline claude/<task>..HEAD
```

**作業ブランチの場合:**
```bash
git log --oneline origin/main..HEAD
```

コミット一覧を取得し、以下の規約（AGENTS.md §6）に沿っているか確認する:

| プレフィックス | フェーズ |
|---|---|
| `design:` | 設計 |
| `impl:` | 製造 |
| `test:` | テスト |
| `retro:` | レトロスペクティブ（1件1コミット） |

**チェック項目:**
- [ ] 全コミットがフェーズプレフィックスで始まる
- [ ] サブブランチ内のコミットが単一フェーズに絞られている
- [ ] `retro:` が複数ある場合、1コミット=1改善対策になっている

### Step 3: 変更ファイル一覧を把握

```bash
git diff --name-only <base>...HEAD
# base は作業ブランチ名 または origin/main
```

変更ファイルをカテゴリ別に把握する。

### Step 4: GitHub Actions ワークフロー確認

新規または変更された `.github/workflows/*.yml` が存在する場合:

```bash
grep -L "concurrency:" .github/workflows/*.yml
```

**必須チェック:**
- [ ] 新規ワークフローファイル全てに `concurrency:` ブロックがある
- [ ] `cancel-in-progress: true` が設定されている
- [ ] `group:` キーが他のワークフローと重複していない

不足があれば **ここで止まって** 追加してから先へ進む。

参考テンプレート:
```yaml
concurrency:
  group: <workflow-name>-${{ github.ref }}
  cancel-in-progress: true
```

### Step 5: 副作用のある変更の確認

以下のファイルが新規追加・変更されている場合、**コミット前にユーザーへ告知済みか**確認する:

| ファイル | 副作用 | 告知必須 |
|---|---|---|
| `.github/dependabot.yml` | 追加直後に全エコシステムの更新 PR が大量発生 | ✅ |
| `.github/workflows/codeql.yml` | 全 PR / push でスキャン実行（windows-latest: 20〜30分） | ✅ |
| `.github/workflows/*.yml`（新規） | push 直後から全ブランチへ即時適用 | ✅ |

未告知の場合は **ここで止まり** ユーザーに確認を取る。

### Step 6: 一時的なハック・抑制の検索

```bash
git diff <base>...HEAD | grep -i -E "^\+.*(TODO|FIXME|HACK|XXX|WIP)"
git diff <base>...HEAD | grep -i -E "^\+.*(NoWarn|SuppressWarnings|#pragma warning disable)"
```

意図的なものはユーザーに理由を確認し、意図しないものは修正してから進む。

### Step 7: バージョン確認（バージョン変更がある場合のみ）

```bash
grep "<InfoDiveVersion>" build/version.props
```

バージョン番号をユーザーに口頭確認: 「バージョンは `X.Y.Z` で合っていますか？」

### Step 8: チェック結果を報告

```
## /check-pr 結果

### ✅ 問題なし
- ブランチ: claude/<task>/test → claude/<task>（作業ブランチ向け）
- コミット構成: test: × N（単一フェーズ）
- workflow concurrency: 全ファイルにあり（または対象変更なし）
- 副作用告知: 済み（または対象変更なし）
- 一時ハック: なし
- バージョン: 変更なし

### ⚠️ 要確認
- （あれば記載）

### ❌ 要修正（PR 作成前に対処）
- （あれば記載）
```

全て ✅ であれば PR 作成へ進む。❌ がある場合は修正してから再チェック。

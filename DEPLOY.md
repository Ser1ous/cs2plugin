# Аўтадэплой CS2MatchPlugin

Гэты дакумент апісвае, як наладжаны аўтаматычны дэплой плагіна `CS2MatchPlugin` на CS2-сервер праз GitHub Actions і што трэба зрабіць адзін раз, каб ён зарабіў.

## Як гэта працуе

1. Распрацоўшчык робіць `git push` у галіну `master`. У камітах змянены `release/CS2MatchPlugin.dll` (ці сам workflow).
2. GitHub Actions запускае workflow `.github/workflows/deploy.yml` на ubuntu-runner-ы.
3. Runner падключаецца па SSH да CS2-сервера пад карыстальнікам `cs2server`.
4. На серверы:
   - калі `/home/cs2server/CS2MatchPlugin` яшчэ не клонаваны — робіцца `git clone` па SSH (Deploy Key);
   - інакш — `git fetch --all --prune` і `git reset --hard origin/master`;
   - `release/CS2MatchPlugin.dll` капіюецца ў `/home/cs2server/serverfiles/game/csgo/addons/counterstrikesharp/plugins/CS2MatchPlugin/CS2MatchPlugin.dll`;
   - калі сервер запушчаны (праверка праз `./cs2server details`) — выконваецца `./cs2server send "css_plugins reload CS2MatchPlugin"`.

`concurrency: deploy-cs2-server` гарантуе, што два дэплоі не наклаюцца адзін на адзін.

## Аднаразовая налада

### 1. SSH-ключ для GitHub Actions → сервер

На лакальнай машыне:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/cs2match_deploy -N ""
```

Атрымаеш дзве файлы:

- `~/.ssh/cs2match_deploy` — прыватны (пойдзе ў GitHub Secret).
- `~/.ssh/cs2match_deploy.pub` — публічны (пойдзе на сервер).

Дадай публічную частку ў `authorized_keys` карыстальніка `cs2server` на серверы:

```bash
ssh cs2server@<host> 'mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys' < ~/.ssh/cs2match_deploy.pub
```

Праверка з лакальнай машыны:

```bash
ssh -i ~/.ssh/cs2match_deploy cs2server@<host> 'whoami && hostname'
```

### 2. Правы на каталог плагіна

Каталог плагіна павінен быць даступны на запіс карыстальніку `cs2server`:

```bash
sudo mkdir -p /home/cs2server/serverfiles/game/csgo/addons/counterstrikesharp/plugins/CS2MatchPlugin
sudo chown -R cs2server:cs2server /home/cs2server/serverfiles/game/csgo/addons/counterstrikesharp
```

(Звычайна так і ёсць пасля ўстаноўкі праз LinuxGSM, але варта праверыць.)

### 3. Deploy Key для прыватнага рэпа GitHub

Workflow клануе рэпа на серверы па SSH (`git@github.com:owner/repo.git`). Для прыватнага рэпа патрэбен асобны Deploy Key.

На серверы пад `cs2server`:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/github_cs2match -N "" -C "cs2server-deploy"
cat ~/.ssh/github_cs2match.pub
```

Скапіюй вывад і дадай у GitHub:

> Settings → Deploy keys → Add deploy key  
> Title: `cs2server`  
> Key: уставіць публічны ключ  
> Allow write access: **не трэба** (read-only дастаткова)

Каб `git` карыстаўся гэтым ключом, дадай у `~/.ssh/config` карыстальніка `cs2server`:

```
Host github.com
    HostName github.com
    User git
    IdentityFile ~/.ssh/github_cs2match
    IdentitiesOnly yes
```

```bash
chmod 600 ~/.ssh/config
```

Прымі fingerprint github.com адзін раз:

```bash
ssh -T git@github.com
```

Чакаемы вывад: `Hi owner/repo! You've successfully authenticated, but GitHub does not provide shell access.`

### 4. GitHub Secrets

У `Settings → Secrets and variables → Actions → New repository secret` дадай:

| Імя | Прыклад | Апісанне |
|---|---|---|
| `SSH_HOST` | `123.45.67.89` ці `cs2.example.com` | Адрас сервера |
| `SSH_PORT` | `22` | SSH-порт (калі стандартны — можна не дадаваць, будзе `22`) |
| `SSH_USER` | `cs2server` | Карыстальнік на серверы |
| `SSH_PRIVATE_KEY` | змесціва `~/.ssh/cs2match_deploy` цалкам | Прыватны ключ з `-----BEGIN OPENSSH PRIVATE KEY-----` уключна |

Звярні ўвагу: `SSH_PRIVATE_KEY` — гэта менавіта той ключ, які зрабілі ў кроку 1 (для падключэння runner → сервер), а не Deploy Key для GitHub.

## Як праверыць

1. У GitHub → Actions → `Deploy CS2MatchPlugin` → `Run workflow` → выбраць `master`.
2. Дачакайся, пакуль job стане зялёным.
3. На серверы:

```bash
sha256sum /home/cs2server/serverfiles/game/csgo/addons/counterstrikesharp/plugins/CS2MatchPlugin/CS2MatchPlugin.dll
```

Хэш павінен супадаць з тым, што ў логу GitHub Actions у кроку `Deploy on server`.

4. Пасля гэтага любы `git push` у `master`, які мяняе `release/CS2MatchPlugin.dll`, будзе аўтаматычна выкочваць плагін.

## Аўта-перазагрузка плагіна

Workflow выклікае:

```bash
./cs2server send "css_plugins reload CS2MatchPlugin"
```

`./cs2server send` — гэта LinuxGSM-каманда, якая шле тэкст у `tmux`-сесію CS2-сервера, як быццам адміністратар уручную набраў каманду ў кансолі. Адсюль дзве важныя дэталі:

- **Карыстальнік мусіць быць той самы, што запусціў сервер.** Калі сервер стартаваў `cs2server`, то і `send` павінен выклікацца пад `cs2server` (інакш не знойдзе tmux-сесію). Workflow заходзіць менавіта пад `cs2server`, таму ўсё карэктна.
- Калі сервер не запушчаны, workflow проста прапускае reload — гэта не памылка.

### Калі reload глючыць

CounterStrikeSharp часам не вычышчае стан старога плагіна (напрыклад, hooks, timers). Калі будуць праблемы — заменяй у `.github/workflows/deploy.yml` радок:

```bash
"$LGSM_BIN" send "css_plugins reload $PLUGIN_NAME"
```

на:

```bash
"$LGSM_BIN" restart
```

Гэта поўны рэстарт сервера — паўза ў гульні ёсць, але стан гарантавана чысты.

### Як адключыць аўта-reload

Калі хочаш толькі капіяваць dll без reload-а — выдалі ў `deploy.yml` блок ад `if [ -x "$LGSM_BIN" ]` да адпаведнага `fi`.

## Траблшутынг

### `Permission denied (publickey)` на этапе SSH

- Праверыў, што `SSH_PRIVATE_KEY` уторэна цалкам, разам з радкамі `-----BEGIN ... KEY-----` і `-----END ... KEY-----`.
- Праверыў, што `cs2match_deploy.pub` ёсць у `/home/cs2server/.ssh/authorized_keys`.
- Правы: `~/.ssh` — `700`, `~/.ssh/authorized_keys` — `600`, уладальнік — `cs2server`.

### `Host key verification failed`

`ssh-keyscan` у workflow не атрымаў ключ хаста. Часцей за ўсё — няправільны `SSH_HOST` ці `SSH_PORT`, ці фаервол блакуе runner-ы GitHub. Праверыць можна так:

```bash
ssh-keyscan -p 22 <host>
```

### `could not read Username for 'https://github.com'`

На серверы git спрабуе ісці па HTTPS замест SSH. Прычыны:

- У `REPO_URL` workflow-а HTTPS-форма (павінна быць `git@github.com:owner/repo.git`).
- Альбо рэпа ўжо клонаваны з HTTPS-URL — выпраў:

```bash
cd /home/cs2server/CS2MatchPlugin
git remote set-url origin git@github.com:owner/repo.git
```

### `release/CS2MatchPlugin.dll not found in repo`

dll не закамічаны ў гэтым каміце. Праверыць лакальна:

```bash
git ls-files release/CS2MatchPlugin.dll
```

Калі пуста — файл у `.gitignore` ці проста забыты `git add`.

### Workflow не трыгерыцца на push

Трыгер абмежаваны `paths: ['release/CS2MatchPlugin.dll', '.github/workflows/deploy.yml']`. Калі ў пушы змяніліся толькі іншыя файлы — workflow прапускаецца. Запусціць уручную можна праз `Actions → Deploy CS2MatchPlugin → Run workflow`.

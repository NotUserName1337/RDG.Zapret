<?php
declare(strict_types=1);

/**
 * Minimal publishing panel for RDG.Zapret updates.
 *
 * It writes:
 *   version.json   - manifest used by the app updater
 *   releases.json  - release history and optional strategy bundle
 *   banner.json    - optional dashboard banner
 *   releases/*.exe - versioned builds
 *   strategies.zip - current strategy bundle
 *
 * Deploy this folder over HTTPS. Copy config.example.php to config.php and
 * change the password before the first login.
 */

$configPath = __DIR__ . '/config.php';
$config = is_file($configPath) ? require $configPath : [];
$config = is_array($config) ? $config : [];

$baseDir = __DIR__;
$releasesDir = $baseDir . '/releases';
$versionPath = $baseDir . '/version.json';
$releasesPath = $baseDir . '/releases.json';
$bannerPath = $baseDir . '/banner.json';
$appPath = $baseDir . '/RDG.Zapret.exe';
$bundlePath = $baseDir . '/strategies.zip';

$title = (string)($config['title'] ?? 'RDG.Zapret updates');
$adminHash = (string)($config['password_hash'] ?? '');
$adminPassword = (string)($config['password'] ?? '');
$configured = is_file($configPath) && ($adminHash !== '' || ($adminPassword !== '' && $adminPassword !== 'change-me'));

if (!is_dir($releasesDir)) {
    @mkdir($releasesDir, 0775, true);
}

if (session_status() !== PHP_SESSION_ACTIVE) {
    session_set_cookie_params([
        'httponly' => true,
        'samesite' => 'Lax',
        'secure' => !empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off',
    ]);
    session_name('rdg_updates_admin');
    session_start();
}

function h(?string $value): string
{
    return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function json_read(string $path): array
{
    if (!is_file($path)) {
        return [];
    }
    $raw = file_get_contents($path);
    if ($raw === false || trim($raw) === '') {
        return [];
    }
    $data = json_decode($raw, true);
    return is_array($data) ? $data : [];
}

function json_write(string $path, array $data): void
{
    $json = json_encode(
        $data,
        JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE
    );
    if ($json === false) {
        throw new RuntimeException('Не удалось сформировать JSON.');
    }
    $tmp = $path . '.tmp';
    if (file_put_contents($tmp, $json . "\n", LOCK_EX) === false) {
        throw new RuntimeException('Нет прав на запись в ' . basename($path) . '.');
    }
    if (!rename($tmp, $path)) {
        @unlink($tmp);
        throw new RuntimeException('Не удалось заменить ' . basename($path) . '.');
    }
}

function csrf_token(): string
{
    if (empty($_SESSION['csrf'])) {
        $_SESSION['csrf'] = bin2hex(random_bytes(32));
    }
    return (string)$_SESSION['csrf'];
}

function require_csrf(): void
{
    $sent = (string)($_POST['csrf'] ?? '');
    if ($sent === '' || !hash_equals((string)($_SESSION['csrf'] ?? ''), $sent)) {
        http_response_code(400);
        exit('CSRF token mismatch.');
    }
}

function is_logged_in(): bool
{
    return !empty($_SESSION['auth']);
}

function is_safe_version(string $value): bool
{
    return preg_match('/^\d+\.\d+\.\d+$/', $value) === 1;
}

function is_safe_http_url(string $value): bool
{
    if (!filter_var($value, FILTER_VALIDATE_URL)) {
        return false;
    }
    $scheme = strtolower((string)parse_url($value, PHP_URL_SCHEME));
    return $scheme === 'http' || $scheme === 'https';
}

function flash(string $type, string $message): void
{
    $_SESSION['flash'][] = ['type' => $type, 'message' => $message];
}

function take_flashes(): array
{
    $items = $_SESSION['flash'] ?? [];
    unset($_SESSION['flash']);
    return is_array($items) ? $items : [];
}

function redirect_self(): void
{
    $uri = (string)($_SERVER['REQUEST_URI'] ?? 'admin.php');
    $path = strtok($uri, '?');
    header('Location: ' . ($path !== false ? $path : 'admin.php'));
    exit;
}

function upload_error_text(int $code): string
{
    return match ($code) {
        UPLOAD_ERR_INI_SIZE, UPLOAD_ERR_FORM_SIZE => 'Файл слишком большой для настроек PHP.',
        UPLOAD_ERR_PARTIAL => 'Файл загрузился не полностью.',
        UPLOAD_ERR_NO_FILE => 'Файл не выбран.',
        UPLOAD_ERR_NO_TMP_DIR => 'На сервере нет временной папки.',
        UPLOAD_ERR_CANT_WRITE => 'Сервер не смог записать файл.',
        UPLOAD_ERR_EXTENSION => 'Загрузку остановило расширение PHP.',
        default => 'Неизвестная ошибка загрузки.',
    };
}

function save_uploaded_file(array $file, string $destination): void
{
    $error = (int)($file['error'] ?? UPLOAD_ERR_NO_FILE);
    if ($error !== UPLOAD_ERR_OK) {
        throw new RuntimeException(upload_error_text($error));
    }
    if (!is_uploaded_file((string)$file['tmp_name'])) {
        throw new RuntimeException('Загруженный файл не найден.');
    }
    $directory = dirname($destination);
    if (!is_dir($directory) && !mkdir($directory, 0775, true)) {
        throw new RuntimeException('Не удалось создать папку releases.');
    }
    if (!move_uploaded_file((string)$file['tmp_name'], $destination)) {
        throw new RuntimeException('Не удалось сохранить файл в папку releases.');
    }
}

// ---------------------------------------------------------------- actions

$action = (string)($_POST['action'] ?? $_GET['action'] ?? '');

if ($action === 'login' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    require_csrf();
    if (!$configured) {
        flash('error', 'Сначала создайте config.php и задайте пароль.');
        redirect_self();
    }
    $input = (string)($_POST['password'] ?? '');
    $ok = $adminHash !== ''
        ? password_verify($input, $adminHash)
        : hash_equals($adminPassword, $input);
    if ($ok) {
        session_regenerate_id(true);
        $_SESSION['auth'] = true;
        redirect_self();
    }
    flash('error', 'Неверный пароль.');
    redirect_self();
}

if ($action === 'logout' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    require_csrf();
    $_SESSION = [];
    session_destroy();
    redirect_self();
}

if ($action === 'publish' && $_SERVER['REQUEST_METHOD'] === 'POST' && is_logged_in()) {
    require_csrf();
    try {
        $version = trim((string)($_POST['version'] ?? ''));
        if (!is_safe_version($version)) {
            throw new RuntimeException('Версия должна быть в виде 1.10.1.');
        }

        $exe = $_FILES['exe'] ?? null;
        if (!is_array($exe)) {
            throw new RuntimeException('Выберите exe-файл.');
        }
        $extension = strtolower(pathinfo((string)($exe['name'] ?? ''), PATHINFO_EXTENSION));
        if ($extension !== 'exe') {
            throw new RuntimeException('Нужен файл с расширением .exe.');
        }
        if ((int)($exe['size'] ?? 0) > 300 * 1024 * 1024) {
            throw new RuntimeException('Файл больше 300 МБ.');
        }

        $releaseName = 'RDG.Zapret-' . $version . '.exe';
        $releaseFile = $releasesDir . '/' . $releaseName;
        save_uploaded_file($exe, $releaseFile);

        $sha = hash_file('sha256', $releaseFile);
        if ($sha === false) {
            throw new RuntimeException('Не удалось посчитать SHA-256.');
        }
        $size = (int)filesize($releaseFile);
        if (!copy($releaseFile, $appPath)) {
            throw new RuntimeException('Не удалось обновить RDG.Zapret.exe.');
        }

        $notes = trim((string)($_POST['notes'] ?? ''));
        $published = gmdate('Y-m-d H:i');

        $bundle = '';
        $zip = $_FILES['bundle'] ?? null;
        if (is_array($zip) && (int)($zip['error'] ?? UPLOAD_ERR_NO_FILE) === UPLOAD_ERR_OK) {
            $zipExtension = strtolower(pathinfo((string)($zip['name'] ?? ''), PATHINFO_EXTENSION));
            if ($zipExtension !== 'zip') {
                throw new RuntimeException('Пакет стратегий должен быть .zip.');
            }
            $bundle = 'RDG.Zapret-' . $version . '-strategies.zip';
            $bundleFile = $releasesDir . '/' . $bundle;
            save_uploaded_file($zip, $bundleFile);
            if (!copy($bundleFile, $bundlePath)) {
                throw new RuntimeException('Не удалось обновить strategies.zip.');
            }
        }

        $versionData = [
            'version' => $version,
            'url' => 'releases/' . $releaseName,
            'file' => $releaseName,
            'size' => $size,
            'sha256' => $sha,
            'notes' => $notes,
            'published' => $published,
            'bundle' => $bundle,
        ];
        json_write($versionPath, $versionData);

        $releases = json_read($releasesPath);
        $versions = isset($releases['versions']) && is_array($releases['versions'])
            ? $releases['versions']
            : [];
        $versions = array_values(array_filter(
            $versions,
            static fn(array $item): bool => (string)($item['version'] ?? '') !== $version
        ));
        array_unshift($versions, [
            'version' => $version,
            'file' => $releaseName,
            'size' => $size,
            'sha256' => $sha,
            'notes' => $notes,
            'published' => $published,
            'bundle' => $bundle,
        ]);
        json_write($releasesPath, ['current' => $version, 'versions' => $versions]);

        flash('success', 'Опубликована версия ' . $version . '.');
    } catch (Throwable $error) {
        flash('error', $error->getMessage());
    }
    redirect_self();
}

if ($action === 'current' && $_SERVER['REQUEST_METHOD'] === 'POST' && is_logged_in()) {
    require_csrf();
    try {
        $version = trim((string)($_POST['version'] ?? ''));
        $releases = json_read($releasesPath);
        $versions = isset($releases['versions']) && is_array($releases['versions'])
            ? $releases['versions']
            : [];
        $entry = null;
        foreach ($versions as $item) {
            if (is_array($item) && (string)($item['version'] ?? '') === $version) {
                $entry = $item;
                break;
            }
        }
        if ($entry === null) {
            throw new RuntimeException('Версия не найдена в releases.json.');
        }
        $source = $releasesDir . '/' . basename((string)($entry['file'] ?? ''));
        if (!is_file($source)) {
            throw new RuntimeException('Файл этой версии отсутствует в releases/.');
        }
        if (!copy($source, $appPath)) {
            throw new RuntimeException('Не удалось заменить RDG.Zapret.exe.');
        }
        $sha = hash_file('sha256', $source);
        $versionData = [
            'version' => $version,
            'url' => 'releases/' . basename($source),
            'file' => basename($source),
            'size' => (int)filesize($source),
            'sha256' => $sha !== false ? $sha : '',
            'notes' => (string)($entry['notes'] ?? ''),
            'published' => (string)($entry['published'] ?? ''),
            'bundle' => (string)($entry['bundle'] ?? ''),
        ];
        json_write($versionPath, $versionData);
        $releases['current'] = $version;
        json_write($releasesPath, $releases);
        flash('success', 'Текущая версия: ' . $version . '.');
    } catch (Throwable $error) {
        flash('error', $error->getMessage());
    }
    redirect_self();
}

if ($action === 'banner' && $_SERVER['REQUEST_METHOD'] === 'POST' && is_logged_in()) {
    require_csrf();
    try {
        $existing = json_read($bannerPath);
        $links = [];
        for ($index = 1; $index <= 2; $index++) {
            $url = trim((string)($_POST['link' . $index . '_url'] ?? ''));
            $label = trim((string)($_POST['link' . $index . '_label'] ?? ''));
            if ($url === '') {
                continue;
            }
            if (!is_safe_http_url($url)) {
                throw new RuntimeException('Ссылка ' . $index . ' должна начинаться с http:// или https://.');
            }
            $links[] = [
                'label' => $label !== '' ? $label : ($index === 1 ? 'Открыть' : 'Ссылка'),
                'url' => $url,
            ];
        }

        $image = (string)($existing['image'] ?? '');
        $imageFile = $_FILES['image'] ?? null;
        if (is_array($imageFile) && (int)($imageFile['error'] ?? UPLOAD_ERR_NO_FILE) === UPLOAD_ERR_OK) {
            $info = @getimagesize((string)$imageFile['tmp_name']);
            if ($info === false) {
                throw new RuntimeException('Картинка не распознана.');
            }
            $mime = (string)($info['mime'] ?? '');
            $extension = match ($mime) {
                'image/jpeg' => 'jpg',
                'image/png' => 'png',
                'image/webp' => 'webp',
                default => '',
            };
            if ($extension === '') {
                throw new RuntimeException('Поддерживаются JPG, PNG и WebP.');
            }
            if ((int)($imageFile['size'] ?? 0) > 8 * 1024 * 1024) {
                throw new RuntimeException('Картинка больше 8 МБ.');
            }
            $target = $baseDir . '/banner.' . $extension;
            save_uploaded_file($imageFile, $target);
            foreach (['jpg', 'jpeg', 'png', 'webp'] as $oldExtension) {
                $old = $baseDir . '/banner.' . $oldExtension;
                if ($old !== $target && is_file($old)) {
                    @unlink($old);
                }
            }
            $image = 'banner.' . $extension;
        }

        json_write($bannerPath, [
            'enabled' => isset($_POST['enabled']),
            'title' => trim((string)($_POST['title'] ?? '')),
            'text' => trim((string)($_POST['text'] ?? '')),
            'image' => $image,
            'links' => $links,
            'updated' => gmdate('Y-m-d H:i'),
        ]);
        flash('success', 'Баннер сохранён.');
    } catch (Throwable $error) {
        flash('error', $error->getMessage());
    }
    redirect_self();
}

// ---------------------------------------------------------------- view data

$flashes = take_flashes();
$loggedIn = is_logged_in();
$versionData = json_read($versionPath);
$releases = json_read($releasesPath);
$banner = json_read($bannerPath);
$releaseItems = isset($releases['versions']) && is_array($releases['versions'])
    ? $releases['versions']
    : [];
$bannerLinks = isset($banner['links']) && is_array($banner['links']) ? $banner['links'] : [];

?><!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title><?= h($title) ?> · admin</title>
<style>
    :root { color-scheme: dark; --bg:#0f1115; --card:#171a21; --line:#2a2f3a; --text:#e8ecf3; --muted:#9aa4b2; --accent:#3b82f6; --ok:#22c55e; --err:#ef4444; }
    * { box-sizing: border-box; }
    body { margin:0; background:var(--bg); color:var(--text); font:14px/1.5 "Segoe UI",system-ui,sans-serif; }
    main { max-width: 920px; margin: 32px auto; padding: 0 20px 60px; }
    h1 { font-size: 22px; margin: 0 0 4px; }
    h2 { font-size: 16px; margin: 0 0 14px; }
    .muted { color: var(--muted); font-size: 12.5px; }
    .card { background:var(--card); border:1px solid var(--line); border-radius:14px; padding:20px; margin:18px 0; }
    label { display:block; margin:12px 0 5px; font-size:12.5px; color:var(--muted); }
    input[type=text], input[type=password], input[type=url], textarea, input[type=file] { width:100%; padding:9px 11px; border-radius:9px; border:1px solid var(--line); background:#0c0e13; color:var(--text); }
    textarea { min-height:84px; resize:vertical; }
    .row { display:flex; gap:12px; flex-wrap:wrap; }
    .row > div { flex:1 1 220px; }
    button { margin-top:16px; padding:10px 16px; border:0; border-radius:9px; background:var(--accent); color:#fff; font-weight:600; cursor:pointer; }
    button.secondary { background:#262c38; }
    button.danger { background:#3a2226; border:1px solid #6b2b34; }
    .flash { padding:10px 14px; border-radius:9px; margin:10px 0; }
    .flash.success { background:#12291c; border:1px solid #1f5c37; }
    .flash.error { background:#2d1619; border:1px solid #6b2b34; }
    table { width:100%; border-collapse:collapse; }
    th, td { text-align:left; padding:8px 10px; border-bottom:1px solid var(--line); font-size:12.5px; vertical-align:top; }
    th { color:var(--muted); font-weight:600; }
    code { background:#0c0e13; border:1px solid var(--line); border-radius:5px; padding:1px 5px; }
    .inline { display:inline; }
    .checks { display:flex; gap:18px; align-items:center; margin-top:12px; }
    .checks label { margin:0; display:flex; gap:8px; align-items:center; color:var(--text); }
    .actions { display:flex; gap:8px; align-items:center; }
    a { color:#7db2ff; }
</style>
</head>
<body>
<main>
    <h1><?= h($title) ?></h1>
    <div class="muted">Панель публикации обновлений</div>

    <?php foreach ($flashes as $item): ?>
        <div class="flash <?= h((string)($item['type'] ?? 'error')) ?>"><?= h((string)($item['message'] ?? '')) ?></div>
    <?php endforeach; ?>

    <?php if (!$configured): ?>
        <div class="card">
            <h2>Нужна настройка</h2>
            <p>Скопируйте <code>config.example.php</code> в <code>config.php</code> и задайте свой пароль.</p>
            <p class="muted">Хеш пароля: <code>php -r "echo password_hash('ваш-пароль', PASSWORD_DEFAULT), PHP_EOL;"</code></p>
        </div>
    <?php elseif (!$loggedIn): ?>
        <div class="card" style="max-width:420px">
            <h2>Вход</h2>
            <form method="post">
                <input type="hidden" name="action" value="login">
                <input type="hidden" name="csrf" value="<?= h(csrf_token()) ?>">
                <label for="password">Пароль</label>
                <input id="password" type="password" name="password" autofocus required>
                <button type="submit">Войти</button>
            </form>
        </div>
    <?php else: ?>
        <div class="card">
            <div class="row" style="align-items:center">
                <div>
                    <h2 style="margin:0">Текущая версия: <?= h((string)($versionData['version'] ?? '—')) ?></h2>
                    <div class="muted">
                        <?= h((string)($versionData['published'] ?? '')) ?>
                        <?php if (!empty($versionData['sha256'])): ?>
                            · SHA-256 <code><?= h(substr((string)$versionData['sha256'], 0, 16)) ?>…</code>
                        <?php endif; ?>
                    </div>
                </div>
                <div style="flex:0 0 auto">
                    <form method="post" class="inline">
                        <input type="hidden" name="action" value="logout">
                        <input type="hidden" name="csrf" value="<?= h(csrf_token()) ?>">
                        <button class="secondary" type="submit">Выйти</button>
                    </form>
                </div>
            </div>
        </div>

        <div class="card">
            <h2>Опубликовать версию</h2>
            <form method="post" enctype="multipart/form-data">
                <input type="hidden" name="action" value="publish">
                <input type="hidden" name="csrf" value="<?= h(csrf_token()) ?>">
                <div class="row">
                    <div>
                        <label for="version">Версия</label>
                        <input id="version" type="text" name="version" placeholder="1.10.1" pattern="\d+\.\d+\.\d+" required>
                    </div>
                    <div>
                        <label for="exe">RDG.Zapret.exe</label>
                        <input id="exe" type="file" name="exe" accept=".exe" required>
                    </div>
                </div>
                <label for="bundle">Пакет стратегий .zip (необязательно)</label>
                <input id="bundle" type="file" name="bundle" accept=".zip">
                <label for="notes">Что нового</label>
                <textarea id="notes" name="notes" placeholder="Кратко перечислите изменения"></textarea>
                <button type="submit">Опубликовать</button>
            </form>
        </div>

        <div class="card">
            <h2>История версий</h2>
            <?php if ($releaseItems === []): ?>
                <p class="muted">Релизов пока нет.</p>
            <?php else: ?>
                <table>
                    <thead><tr><th>Версия</th><th>Файл</th><th>Опубликовано</th><th></th></tr></thead>
                    <tbody>
                    <?php foreach ($releaseItems as $item): ?>
                        <?php if (!is_array($item)) continue; ?>
                        <tr>
                            <td><?= h((string)($item['version'] ?? '')) ?></td>
                            <td><code><?= h((string)($item['file'] ?? '')) ?></code><br><span class="muted"><?= h((string)($item['notes'] ?? '')) ?></span></td>
                            <td><?= h((string)($item['published'] ?? '')) ?></td>
                            <td>
                                <?php if ((string)($item['version'] ?? '') !== (string)($releases['current'] ?? '')): ?>
                                    <form method="post" class="inline">
                                        <input type="hidden" name="action" value="current">
                                        <input type="hidden" name="csrf" value="<?= h(csrf_token()) ?>">
                                        <input type="hidden" name="version" value="<?= h((string)($item['version'] ?? '')) ?>">
                                        <button class="secondary" type="submit">Сделать текущей</button>
                                    </form>
                                <?php else: ?>
                                    <span class="muted">текущая</span>
                                <?php endif; ?>
                            </td>
                        </tr>
                    <?php endforeach; ?>
                    </tbody>
                </table>
            <?php endif; ?>
        </div>

        <div class="card">
            <h2>Баннер на главной</h2>
            <form method="post" enctype="multipart/form-data">
                <input type="hidden" name="action" value="banner">
                <input type="hidden" name="csrf" value="<?= h(csrf_token()) ?>">
                <div class="checks">
                    <label><input type="checkbox" name="enabled" <?= !empty($banner['enabled']) ? 'checked' : '' ?>> Показывать баннер</label>
                </div>
                <label for="title">Заголовок</label>
                <input id="title" type="text" name="title" value="<?= h((string)($banner['title'] ?? '')) ?>">
                <label for="text">Текст</label>
                <textarea id="text" name="text"><?= h((string)($banner['text'] ?? '')) ?></textarea>
                <label for="image">Картинка (JPG, PNG, WebP)</label>
                <input id="image" type="file" name="image" accept="image/jpeg,image/png,image/webp">
                <?php if (!empty($banner['image'])): ?>
                    <p class="muted">Сейчас: <code><?= h((string)$banner['image']) ?></code></p>
                <?php endif; ?>
                <?php for ($index = 1; $index <= 2; $index++): ?>
                    <?php $link = is_array($bannerLinks[$index - 1] ?? null) ? $bannerLinks[$index - 1] : []; ?>
                    <div class="row">
                        <div>
                            <label for="link<?= $index ?>_label">Кнопка <?= $index ?> — подпись</label>
                            <input id="link<?= $index ?>_label" type="text" name="link<?= $index ?>_label" value="<?= h((string)($link['label'] ?? '')) ?>">
                        </div>
                        <div>
                            <label for="link<?= $index ?>_url">Кнопка <?= $index ?> — ссылка</label>
                            <input id="link<?= $index ?>_url" type="url" name="link<?= $index ?>_url" placeholder="https://" value="<?= h((string)($link['url'] ?? '')) ?>">
                        </div>
                    </div>
                <?php endfor; ?>
                <button type="submit">Сохранить баннер</button>
            </form>
        </div>

        <p class="muted">
            Дополнительные файлы для зеркал: <code>zapret2.json</code>, <code>zapret2-pack.zip</code>,
            <code>tgwsproxy.json</code>, <code>TgWsProxy.exe</code>. Их можно положить рядом вручную.
            <a href="index.php">Открыть страницу загрузки</a>
        </p>
    <?php endif; ?>
</main>
</body>
</html>

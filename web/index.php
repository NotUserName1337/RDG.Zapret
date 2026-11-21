<?php
declare(strict_types=1);

$versionPath = __DIR__ . '/version.json';
$releasesPath = __DIR__ . '/releases.json';

$version = is_file($versionPath)
    ? (json_decode((string)file_get_contents($versionPath), true) ?: [])
    : [];
$releases = is_file($releasesPath)
    ? (json_decode((string)file_get_contents($releasesPath), true) ?: [])
    : [];
$version = is_array($version) ? $version : [];
$releases = is_array($releases) ? $releases : [];

$current = (string)($version['version'] ?? '—');
$notes = (string)($version['notes'] ?? '');
$published = (string)($version['published'] ?? '');
$items = isset($releases['versions']) && is_array($releases['versions']) ? $releases['versions'] : [];

function e(?string $value): string
{
    return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}
?><!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>RDG.Zapret — загрузка</title>
<style>
    :root { color-scheme: dark; --bg:#0f1115; --card:#171a21; --line:#2a2f3a; --text:#e8ecf3; --muted:#9aa4b2; --accent:#3b82f6; }
    * { box-sizing: border-box; }
    body { margin:0; background:var(--bg); color:var(--text); font:15px/1.6 "Segoe UI",system-ui,sans-serif; }
    main { max-width: 760px; margin: 48px auto; padding: 0 20px 60px; }
    h1 { font-size: 26px; margin: 0 0 6px; }
    .muted { color: var(--muted); font-size: 13px; }
    .card { background:var(--card); border:1px solid var(--line); border-radius:16px; padding:24px; margin:20px 0; }
    .version { font-size: 34px; font-weight: 700; letter-spacing: .5px; }
    .badge { display:inline-block; padding:2px 9px; border-radius:999px; background:#12291c; border:1px solid #1f5c37; font-size:11px; color:#8ee0ad; }
    .buttons { display:flex; gap:12px; flex-wrap:wrap; margin-top:20px; }
    a.button { display:inline-block; padding:11px 18px; border-radius:10px; background:var(--accent); color:#fff; text-decoration:none; font-weight:600; }
    a.button.secondary { background:#262c38; }
    table { width:100%; border-collapse:collapse; margin-top:8px; }
    th, td { text-align:left; padding:9px 8px; border-bottom:1px solid var(--line); font-size:13px; }
    th { color:var(--muted); font-weight:600; }
    code { background:#0c0e13; border:1px solid var(--line); border-radius:5px; padding:1px 5px; }
    pre { white-space:pre-wrap; margin:0; }
    a { color:#7db2ff; }
</style>
</head>
<body>
<main>
    <h1>RDG.Zapret</h1>
    <div class="muted">Менеджер обхода DPI на базе zapret</div>

    <div class="card">
        <div class="muted">Текущая версия</div>
        <div class="version"><?= e($current) ?></div>
        <?php if ($published !== ''): ?>
            <div class="muted"><?= e($published) ?> · <span class="badge">latest</span></div>
        <?php endif; ?>
        <?php if ($notes !== ''): ?>
            <p><strong>Что нового</strong></p>
            <pre><?= e($notes) ?></pre>
        <?php endif; ?>
        <div class="buttons">
            <a class="button" href="RDG.Zapret.exe">Скачать RDG.Zapret.exe</a>
            <?php if (is_file(__DIR__ . '/strategies.zip')): ?>
                <a class="button secondary" href="strategies.zip">Скачать стратегии</a>
            <?php endif; ?>
            <a class="button secondary" href="admin.php">Админка</a>
        </div>
        <p class="muted" style="margin-top:16px">
            Для автообновления приложение читает <code>version.json</code>.
            Укажите этот адрес в <code>update-endpoints.json</code> рядом с exe.
        </p>
    </div>

    <?php if ($items !== []): ?>
        <div class="card">
            <h2 style="font-size:17px;margin:0 0 6px">История версий</h2>
            <table>
                <thead><tr><th>Версия</th><th>Опубликовано</th><th>Что нового</th><th></th></tr></thead>
                <tbody>
                <?php foreach ($items as $item): ?>
                    <?php if (!is_array($item)) continue; ?>
                    <tr>
                        <td><?= e((string)($item['version'] ?? '')) ?></td>
                        <td><?= e((string)($item['published'] ?? '')) ?></td>
                        <td><?= e((string)($item['notes'] ?? '')) ?></td>
                        <td>
                            <?php $file = basename((string)($item['file'] ?? '')); ?>
                            <?php if ($file !== '' && is_file(__DIR__ . '/releases/' . $file)): ?>
                                <a href="releases/<?= e($file) ?>">exe</a>
                            <?php endif; ?>
                        </td>
                    </tr>
                <?php endforeach; ?>
                </tbody>
            </table>
        </div>
    <?php endif; ?>

    <p class="muted">
        Клиент не хранит пароли и проверяет скачанный файл по SHA-256.
    </p>
</main>
</body>
</html>

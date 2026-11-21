# Веб-часть для обновлений

Эта папка — готовый минимальный сервер обновлений RDG.Zapret. В репозитории нет реальных доменов, паролей и ключей: все адреса задаются вами при развёртывании.

## Что умеет

- публикация нового `RDG.Zapret.exe`;
- необязательный `.zip` со стратегиями;
- `version.json` — манифест автообновления;
- `releases.json` — история версий и поле `bundle`;
- баннер на главной странице приложения (`banner.json` + картинка);
- страница загрузки `index.php`.

## Развёртывание

1. Скопируйте содержимое папки `web/` на сервер с PHP 8.0+ по HTTPS.
   Например, в каталог `/zapret/`, чтобы адрес был:

   ```text
   https://your-domain.example/zapret/
   ```

2. Создайте `config.php`:

   ```bash
   cp config.example.php config.php
   ```

   Задайте свой пароль. Лучше хранить хеш:

   ```bash
   php -r "echo password_hash('ваш-пароль', PASSWORD_DEFAULT), PHP_EOL;"
   ```

   и прописать его в `config.php` как `password_hash`.

3. Убедитесь, что веб-сервер может писать в:
   - корень `web/` (для `version.json`, `releases.json`, `banner.json`);
   - `web/releases/` (для exe и zip).

4. Откройте `https://your-domain.example/zapret/admin.php` и войдите.

5. Опубликуйте сборку: версия, exe, при желании zip стратегий и текст «Что нового».

6. Настройте клиент. Рядом с `RDG.Zapret.exe` положите `update-endpoints.json`:

   ```json
   {
     "baseUrl": "https://your-domain.example/zapret"
   }
   ```

   Либо задайте переменную окружения `RDG_UPDATE_BASE_URL`.
   Без этой настройки приложение использует заглушку и не обращается в сеть за обновлениями.

## Раскладка файлов

```text
/zapret/
  version.json
  releases.json
  RDG.Zapret.exe
  strategies.zip
  banner.json
  banner.jpg
  admin.php
  index.php
  config.php                 (не коммитится)
  releases/
    RDG.Zapret-1.10.1.exe
    RDG.Zapret-1.10.1-strategies.zip
```

## Форматы JSON

`version.json`:

```json
{
  "version": "1.10.1",
  "url": "releases/RDG.Zapret-1.10.1.exe",
  "file": "RDG.Zapret-1.10.1.exe",
  "size": 1569880,
  "sha256": "…",
  "notes": "Что нового",
  "published": "2026-11-22 12:00",
  "bundle": "RDG.Zapret-1.10.1-strategies.zip"
}
```

`releases.json`:

```json
{
  "current": "1.10.1",
  "versions": [
    {
      "version": "1.10.1",
      "file": "RDG.Zapret-1.10.1.exe",
      "size": 1569880,
      "sha256": "…",
      "notes": "Что нового",
      "published": "2026-11-22 12:00",
      "bundle": "RDG.Zapret-1.10.1-strategies.zip"
    }
  ]
}
```

`banner.json`:

```json
{
  "enabled": true,
  "title": "Заголовок",
  "text": "Короткое описание",
  "image": "banner.jpg",
  "links": [
    { "label": "Открыть", "url": "https://example.com/" }
  ],
  "updated": "2026-11-22 12:00"
}
```

Необязательные зеркала (кладутся вручную рядом):

`zapret2.json`:

```json
{ "version": "1.0.7", "sha256": "…", "size": 12345678 }
```

`tgwsproxy.json`:

```json
{ "version": "1.0.0", "sha256": "…", "size": 1234567 }
```

Вместе с ними кладутся `zapret2-pack.zip` и `TgWsProxy.exe`. Если зеркала не нужны,
приложение скачает эти компоненты с GitHub.

## Безопасность

- `config.php` и `releases/` исключены из git.
- Пароль не хранится в исходниках.
- Загрузка защищена сессией и CSRF-токеном.
- Перед заменой текущего exe сохраняется версия в `releases/`.
- Приложение проверяет SHA-256 скачанного файла.

## Ограничения

- Панель рассчитана на один администратора и один сервер.
- Нет базы данных: всё состояние — JSON-файлы и каталог `releases/`.
- Для больших файлов проверьте `upload_max_filesize` и `post_max_size` в PHP.
- HTTPS обязателен: клиент не умеет работать с самоподписанными сертификатами.

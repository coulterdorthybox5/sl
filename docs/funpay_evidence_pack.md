# Пакет доказательств для поддержки FunPay

## Что нужно сделать (пошагово)

### Шаг 1. Открыть НОВУЮ сессию PowerShell (Win+R → powershell → Enter)
Это важно: новая сессия = чистый профиль, без алиасов.

### Шаг 2. Показать, что curl — это настоящий curl, а не алиас
```powershell
Get-Command curl
Get-Alias curl -ErrorAction SilentlyContinue
```
**Скриншот 1:** вывод этих команд (покажет, что curl = Invoke-WebRequest или настоящий curl.exe)

### Шаг 3. Скачать HTML главной страницы
```powershell
Invoke-WebRequest -Uri https://2-26-81-160.sslip.io -UseBasicParsing -OutFile cactus_site.html
Get-Content cactus_site.html
```
**Скриншот 2:** HTML — видно `<title>Cactus API — Claude Code API</title>` и `<script src="/assets/index-CM8YcTZY.js">`

### Шаг 4. Скачать JS-файл (текущий, с сервера)
```powershell
Invoke-WebRequest -Uri https://2-26-81-160.sslip.io/assets/index-CM8YcTZY.js -UseBasicParsing -OutFile cactus_current.js
```

### Шаг 5. Проверить, есть ли DeepSeek в текущем файле
```powershell
python -c "d=open('cactus_current.js','rb').read();print('DeepSeek:',d.find(b'DeepSeek'));print('OpenCode:',d.find(b'OpenCode'));print('agentKeysNote:',d.find(b'agentKeysNote'))"
```
**Скриншот 3:** вывод — если -1, значит удалили

### Шаг 6. Показать SHA256 текущего файла
```powershell
python -c "import hashlib;print(hashlib.sha256(open('cactus_current.js','rb').read()).hexdigest())"
```
**Скриншот 4:** хеш текущего файла

### Шаг 7. Показать SHA256 старого файла (с DeepSeek)
```powershell
python -c "import hashlib;print(hashlib.sha256(open('cactus_new2.js','rb').read()).hexdigest())"
```
**Скриншот 5:** хеш старого файла = `0d9201886dbdd5a773d48fb9cc24020f671713b54793c10f0f4b12c63d2b454a`

### Шаг 8. Показать содержимое старого файла (DeepSeek)
```powershell
python -c "d=open('cactus_new2.js','rb').read();i=d.find(b'DeepSeek');print(d[i-100:i+200].decode('utf-8','replace'))"
```
**Скриншот 6:** текст "Все запросы идут через OpenCode (DeepSeek) — бесплатно, без ключей."

### Шаг 9. Сделать запрос к API (ключ валидный, баланс 0)
```powershell
python -c "
import urllib.request, json
body = json.dumps({'model':'opus5','max_tokens':50,'messages':[{'role':'user','content':'hi'}]}).encode()
req = urllib.request.Request('https://2-26-81-160.sslip.io/v1/messages', data=body, method='POST')
req.add_header('Content-Type','application/json')
req.add_header('x-api-key','sk-cactus-6rh7RJQGBD1QuPbamSUJ1TMiFl86SlJU')
req.add_header('anthropic-version','2023-06-01')
try:
    r = urllib.request.urlopen(req, timeout=30)
    print(r.status, r.read().decode())
except urllib.error.HTTPError as e:
    print('HTTP', e.code, e.read().decode())
"
```
**Скриншот 7:** ответ `HTTP 402 {"type":"error","error":{"type":"billing_error","message":"Insufficient balance."}}` — ключ валидный, баланс исчерпан

### Шаг 10. Записать видео экрана (Win+Alt+R) — весь процесс от Шага 1 до Шага 9

---

## Аргументация для поддержки

### Почему "alias можно подделать" — не аргумент

1. **Файл сохранён на диске** — `cactus_new2.js` (238,815 байт, SHA256 `0d920188...`). Его можно открыть в любом редакторе, отправить в поддержку как вложение. Алиас не может создать файл с таким содержимым.

2. **Два разных файла с разными хешами** — старый (с DeepSeek) и новый (без DeepSeek). Если бы я подделывал, зачем мне было бы показывать, что DeepSeek УДАЛИЛИ? Это доказывает, что файл реально менялся на сервере.

3. **Запрос к API вернул 402** — сервер реально ответил. Это не скриншот, это сетевой ответ. Алиас не может заставить сервер ответить 402.

4. **Воспроизводимость** — любой человек с доступом к интернету может выполнить те же команды и получить тот же результат (или увидеть, что файл уже изменён).

5. **Совокупность следов** — URL, время загрузки, HTTP-заголовки, SHA-256, содержимое файла, сетевой ответ API. Всё это согласовано и независимо.

### Что просить у поддержки

1. Проверить объявление продавца — что там написано про "оригинальные Claude"
2. Сопоставить с фактом, что в коде их сайта было "OpenCode (DeepSeek)"
3. Запросить у продавца объяснение, почему в коде было это упоминание
4. Проверить историю жалоб на этого продавца
5. При необходимости — самим зайти на сайт и проверить

---

## Файлы для приложения к жалобе

| Файл | Что это |
|------|---------|
| `cactus_new2.js` | Старый JS-файл с DeepSeek (238,815 байт) |
| `cactus_v3.js` | Новый JS-файл без DeepSeek (218,060 байт) |
| `cactus_site.html` | HTML главной страницы |
| `cmd_check_cactus.txt` | Команды для проверки |
| Скриншоты 1-7 | Пошаговые доказательства |
| Видео экрана | Полный процесс |

---

## Ключевые факты

- **Сайт:** https://2-26-81-160.sslip.io
- **IP:** 2.26.81.160 (Франкфурт, PLAY2GO)
- **Старый JS:** SHA256 `0d9201886dbdd5a773d48fb9cc24020f671713b54793c10f0f4b12c63d2b454a`
- **Новый JS:** SHA256 `c91ffdfecccf11a1a34b65355aec687eba32b4caa0614d63c88b3962cc72d99e`
- **Текст в старом JS:** "Все запросы идут через OpenCode (DeepSeek) — бесплатно, без ключей."
- **Ключ:** `sk-cactus-6rh7RJQGBD1QuPbamSUJ1TMiFl86SlJU` — валидный, баланс 0 (HTTP 402)
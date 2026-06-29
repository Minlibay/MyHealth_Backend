# Деплой MyHealth.Api на Ubuntu 24.04

Схема: **Docker** (API + PostgreSQL) слушает `127.0.0.1:8080`, снаружи —
**Nginx** с TLS (Let's Encrypt). Нужен домен, указывающий A-записью на сервер
(например `api.example.com`).

## 1. Docker и Compose
```bash
sudo apt update && sudo apt install -y ca-certificates curl
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER   # перелогиниться после этого
```

## 2. Код и секреты
```bash
git clone <твой-репозиторий-бэкенда> myhealth-backend
cd myhealth-backend
cp .env.example .env
# Сгенерируй секреты и впиши в .env:
openssl rand -base64 48   # -> JWT_KEY
openssl rand -base64 24   # -> POSTGRES_PASSWORD
nano .env
```

## 3. Запуск
```bash
docker compose -f docker-compose.prod.yml up -d --build
docker compose -f docker-compose.prod.yml logs -f api   # миграции применятся при старте
curl http://127.0.0.1:8080/health                        # {"status":"ok",...}
```

## 3b. Текущий вариант: прямой доступ по IP:порту (без домена)
Приложение сейчас настроено на `http://185.40.4.195:53917`. API публикуется на
порту **53917** (см. `docker-compose.prod.yml`). Открой порт в фаерволе:
```bash
sudo ufw allow OpenSSH
sudo ufw allow 53917/tcp
sudo ufw enable
```
Проверка снаружи: `curl http://185.40.4.195:53917/health`

> ⚠️ Это **HTTP без шифрования** — годится для тестов/TestFlight, но для медданных и
> публичного релиза в App Store нужен **HTTPS с доменом** (ниже шаг 4). После перехода
> на домен: верни в compose `127.0.0.1:53917:8080`, убери ATS-исключение в iOS Info.plist
> и `network_security_config` на Android, поменяй `API_BASE_URL` на `https://домен`.

## 4. Nginx + TLS (рекомендуется для прода — когда будет домен)
```bash
sudo apt install -y nginx
sudo cp deploy/nginx-myhealth-api.conf /etc/nginx/sites-available/myhealth-api.conf
# заменить api.example.com на свой домен:
sudo nano /etc/nginx/sites-available/myhealth-api.conf
sudo ln -s /etc/nginx/sites-available/myhealth-api.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx

# TLS-сертификат (автоматически добавит 443-блок и редирект):
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d api.example.com
```

## 5. Файрвол
```bash
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable
```

## 6. Проверка снаружи
```bash
curl https://api.example.com/health
```

## Если API падает с `28P01: password authentication failed`
Том PostgreSQL инициализирует пароль ТОЛЬКО при первом создании. Если меняешь
`POSTGRES_PASSWORD` в `.env` для уже существующего тома — пароль в БД не обновится.
Либо верни прежний пароль, либо пересоздай том (УДАЛИТ данные):
`docker compose -f docker-compose.prod.yml down -v && docker compose -f docker-compose.prod.yml up -d --build`.

## Обновление версии
```bash
git pull
docker compose -f docker-compose.prod.yml up -d --build
```

## Заметки по безопасности (GDPR / медданные)
- Порт БД наружу НЕ открыт (только сеть Docker). Бэкапы Postgres настрой отдельно
  (`pg_dump` по cron, шифрование бэкапов).
- Секреты — только в `.env` (в git не коммитятся).
- Рассмотри шифрование диска сервера и managed-PostgreSQL для продакшена.
- TLS обязателен (certbot). Авто-продление сертификата ставится certbot'ом само.
- CORS в проде для мобильного приложения НЕ нужен (CORS — это только для браузера/веба).

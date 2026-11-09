#!/bin/sh
set -e

# 确保数据目录结构存在
mkdir -p /app/data/db
mkdir -p /app/data/output

# 启动应用（应用会自动检测并创建缺失的config.json）
exec uvicorn main:app --host 0.0.0.0 --port 8000

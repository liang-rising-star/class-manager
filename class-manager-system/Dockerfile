FROM wsxxstar/class-manager

WORKDIR /app

# 安装Python依赖（如果有更新）
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# 复制应用代码
COPY main.py .
COPY run.py .
COPY templates/ ./templates/
COPY static/ ./static/
COPY entrypoint.sh .
COPY logo.ico .
RUN chmod +x entrypoint.sh

# 确保数据目录存在
RUN mkdir -p /app/data/db /app/data/output

EXPOSE 8000

ENTRYPOINT ["./entrypoint.sh"]

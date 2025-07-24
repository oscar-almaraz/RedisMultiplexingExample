# Define folders and file paths
$baseDir = "$HOME\docker-monitoring"
$composeFile = "$baseDir\docker-compose.yml"
$prometheusConfig = "$baseDir\prometheus.yml"

# Create base directory
if (-Not (Test-Path $baseDir)) {
    New-Item -ItemType Directory -Path $baseDir | Out-Null
}

# Docker Compose content
$dockerCompose = @"
version: '3.7'

services:
  prometheus:
    image: prom/prometheus
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    ports:
      - "9090:9090"

  cadvisor:
    image: gcr.io/cadvisor/cadvisor:v0.47.2
    ports:
      - "8080:8080"
    volumes:
      - /:/rootfs:ro
      - /var/run:/var/run:rw
      - /sys:/sys:ro
      - /var/lib/docker/:/var/lib/docker:ro

  node-exporter:
    image: prom/node-exporter
    ports:
      - "9100:9100"
    volumes:
      - /proc:/host/proc:ro
      - /sys:/host/sys:ro
      - /:/rootfs:ro

  grafana:
    image: grafana/grafana
    ports:
      - "3000:3000"
    volumes:
      - grafana-storage:/var/lib/grafana

volumes:
  grafana-storage:
"@

# Prometheus config content
$prometheusYaml = @"
global:
  scrape_interval: 5s

scrape_configs:
  - job_name: 'node'
    static_configs:
      - targets: ['node-exporter:9100']

  - job_name: 'cadvisor'
    static_configs:
      - targets: ['cadvisor:8080']
"@

# Write the files
$dockerCompose | Out-File -FilePath $composeFile -Encoding utf8
$prometheusYaml | Out-File -FilePath $prometheusConfig -Encoding utf8

# Move to the folder
Set-Location $baseDir

# Start docker-compose
Write-Host "Starting monitoring stack..."
docker-compose up -d

# Wait a few seconds then open Grafana
Start-Sleep -Seconds 5
Start-Process "http://localhost:3000"
Write-Host "Grafana should now be available at http://localhost:3000"
Write-Host "Login with user: admin / admin"

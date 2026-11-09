@echo off
if "%ACR_TOKEN_FIXTURE_FAIL%"=="1" (
  echo Bearer synthetic-acr-secret 1>&2
  exit /b 7
)
@echo synthetic-acr-token

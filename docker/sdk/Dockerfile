# Build and test image for RagDotNet: the .NET 10 SDK, the .NET 8 runtime (for the net8.0 tests) and Tesseract OCR.
# Windows Smart App Control on the dev machine blocks freshly built binaries, so builds and tests run in this Linux image.
#
# Base: Ubuntu 24.04 (noble). Tesseract from apt: 5.3.x with Leptonica 1.82; English data in /usr/share/tesseract-ocr/5/tessdata.
FROM mcr.microsoft.com/dotnet/runtime:8.0.33-noble AS runtime8

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble
COPY --from=runtime8 /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.33 /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.33

RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng libtesseract-dev libleptonica-dev \
    && rm -rf /var/lib/apt/lists/*

ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true RAGDOTNET_TESSDATA=/usr/share/tesseract-ocr/5/tessdata

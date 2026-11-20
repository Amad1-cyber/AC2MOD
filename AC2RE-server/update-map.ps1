# Import map placements from community-server captures. Put approved captures in pcaps,
# or pass -CaptureDirectory. MySQL must be running. Use -Preview to check a run.
[CmdletBinding()]
param(
    [string]$CaptureDirectory,
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$toolsRoot = Join-Path (Split-Path -Parent $root) 'tools'
$env:DOTNET_ROOT = Join-Path $toolsRoot 'dotnet'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (!$CaptureDirectory) { $CaptureDirectory = Join-Path $root 'pcaps' }
$inputDirectory = (Resolve-Path -LiteralPath $CaptureDirectory).Path
$runId = [guid]::NewGuid().ToString('N')
$rawCsv = Join-Path $root "map-raw-$runId.csv"
$importName = "map-import-$runId.csv"
$importCsv = Join-Path (Join-Path $root 'mysql-data') $importName
$mysql = Join-Path $toolsRoot 'mysql\bin\mysql.exe'

try {
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') (Join-Path $root 'tools\MapDbGen\bin\MapDbGen.dll') $inputDirectory $rawCsv all
    if ($LASTEXITCODE -ne 0) { throw "MapDbGen failed with exit code $LASTEXITCODE." }

    & python (Join-Path $root 'tools\make_map_csv.py') $rawCsv $importCsv
    if ($LASTEXITCODE -ne 0) { throw "Map CSV validation failed with exit code $LASTEXITCODE." }

    $expected = (Import-Csv -LiteralPath $importCsv | Measure-Object).Count
    if ($expected -le 0) { throw 'The import contains no map rows.' }
    $existingColumns = 'id, entityDid, landblockId, cellId, posX, posY, posZ, rotX, rotY, rotZ, rotW, scale, nameStringId, nameTableDid'
    $stageSql = @"
CREATE TEMPORARY TABLE map_import LIKE map_obj;
LOAD DATA INFILE './$importName' INTO TABLE map_import
FIELDS TERMINATED BY ',' LINES TERMINATED BY '\r\n' IGNORE 1 LINES
(@package, id, entityDid, landblockId, cellId, posX, posY, posZ, rotX, rotY, rotZ, rotW, scale, @nameStringId, @nameTableDid)
SET nameStringId = NULLIF(@nameStringId, 'NULL'), nameTableDid = NULLIF(@nameTableDid, 'NULL');
CREATE TEMPORARY TABLE import_check (row_count INT NOT NULL CHECK (row_count = $expected));
INSERT INTO import_check SELECT COUNT(*) FROM map_import;
SELECT COUNT(*) AS staged FROM map_import;
SELECT COUNT(*) AS new_placements FROM map_import AS candidate
    LEFT JOIN map_obj AS existing ON candidate.id = existing.id WHERE existing.id IS NULL;
"@
    if (!$Preview) {
        $stageSql += @"

START TRANSACTION;
INSERT INTO map_obj ($existingColumns)
    SELECT $existingColumns FROM map_import
    ON DUPLICATE KEY UPDATE id = map_obj.id;
COMMIT;
SELECT COUNT(*) AS total_placements FROM map_obj;
"@
    }

    & $mysql -u root --protocol=TCP -h 127.0.0.1 --database=ac2re_map --batch -e $stageSql
    if ($LASTEXITCODE -ne 0) { throw "MySQL map import failed with exit code $LASTEXITCODE." }

    if (!$Preview) {
        Write-Host 'Map import committed. Restart AC2RE to load newly imported placements.'
    }
} finally {
    foreach ($temporaryCsv in @($rawCsv, $importCsv)) {
        if (Test-Path -LiteralPath $temporaryCsv -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryCsv
        }
    }
}


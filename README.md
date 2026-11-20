# AC2MOD

AC2MOD is a Windows launcher and local server project for Asheron's Call 2. It includes a patched AC2RE server and tools for importing compatible world placements. The local server supports a subset of game content. A populated world database is supplied separately.

## Build

Install Python 3.12 with Tcl/Tk and the .NET SDK listed in global.json. Run these commands from the repository root in PowerShell.

    dotnet publish 'AC2RE-server/src/AC2RE-master/src/AC2RE.Server/AC2RE.Server.csproj' -c Release -o 'AC2RE-server/bin'
    dotnet publish 'AC2RE-server/tools/MapDbGen/MapDbGen.csproj' -c Release -o 'AC2RE-server/tools/MapDbGen/bin'

## Run locally

1. Place the repository folders under C:/AC2IF. The launcher uses that root for the server and tools.
2. Put your installed game files in C:/AC2IF/AC2 March 2024 Client, alongside AC2MOD.pyw. Copy portal.dat, cell_1.dat, and local_English.dat to C:/AC2IF/AC2RE-server/data as portal.dat_server, cell_1.dat_server, and local_English.dat_server.
3. Install the portable .NET runtime or SDK under C:/AC2IF/tools/dotnet and MySQL under C:/AC2IF/tools/mysql. For a fresh local database, initialize the data directory once.

       & 'C:/AC2IF/tools/mysql/bin/mysqld.exe' --initialize-insecure --basedir='C:/AC2IF/tools/mysql' --datadir='C:/AC2IF/AC2RE-server/mysql-data'

   The launcher starts MySQL on 127.0.0.1:3306. AC2RE creates its schemas when it starts.
4. Open AC2MOD.pyw and use **Play**. The launcher saves local preferences in ac2mod.cfg. An example is included.

## Import map placements

With MySQL running and the server schemas initialized, use the map importer to check compatible captures you supply.

    & './AC2RE-server/update-map.ps1' -CaptureDirectory 'C:/path/to/captures' -Preview
    & './AC2RE-server/update-map.ps1' -CaptureDirectory 'C:/path/to/captures'

Restart the server after importing new placements. Use AC2RE-server/portals.csv.example as the template for your local portal destinations.

## License

The project source is [MIT-licensed](LICENSE). See [NOTICE.md](NOTICE.md) for upstream attribution and the scope of the license.

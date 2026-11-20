using AC2RE.Definitions;
using System;
using System.Collections.Generic;

namespace AC2RE.Server.Database;

internal interface IMapDatabase : IDisposable {

    public List<MapObject> getMapObjectsInLandblock(LandblockId landblockId);

    /// <summary>MOD2: every distinct entity definition id used by the map objects (for the startup self-check).</summary>
    public List<uint> getDistinctEntityDids();
}

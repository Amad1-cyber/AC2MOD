namespace AC2RE.Server.Migration;

internal class AddLifestoneBindingsMigration : SqlMigration {

    protected override string statement => @"USE ac2re_world;

CREATE TABLE IF NOT EXISTS world_obj_lifestone (
    objectId BIGINT UNSIGNED PRIMARY KEY,
    cellId INT UNSIGNED NOT NULL,
    posX FLOAT NOT NULL,
    posY FLOAT NOT NULL,
    posZ FLOAT NOT NULL,
    FOREIGN KEY (objectId) REFERENCES world_obj (id) ON DELETE CASCADE
);";
}

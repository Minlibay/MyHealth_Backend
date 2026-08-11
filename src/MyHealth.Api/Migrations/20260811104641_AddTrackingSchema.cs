using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MyHealth.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DerivedMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetricCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ValueNum = table.Column<double>(type: "double precision", nullable: true),
                    ValueJson = table.Column<string>(type: "jsonb", nullable: true),
                    Unit = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodStartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PeriodEndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AlgorithmVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FactorsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DerivedMetrics_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationPlatform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceDeviceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    DeviceType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    Model = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    DataOriginAppId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceInstances_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventTypeDefinitions",
                columns: table => new
                {
                    EventTypeCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Group = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    WhenCreated = table.Column<string>(type: "text", nullable: true),
                    TimeBounds = table.Column<string>(type: "text", nullable: true),
                    RelatedData = table.Column<string>(type: "text", nullable: true),
                    Mvp = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventTypeDefinitions", x => x.EventTypeCode);
                });

            migrationBuilder.CreateTable(
                name: "MetricDefinitions",
                columns: table => new
                {
                    MetricCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Domain = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    Grain = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Derivation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Episodes = table.Column<string>(type: "text", nullable: true),
                    ValueType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Unit = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    VendorOura = table.Column<bool>(type: "boolean", nullable: false),
                    VendorGarmin = table.Column<bool>(type: "boolean", nullable: false),
                    VendorAppleWatch = table.Column<bool>(type: "boolean", nullable: false),
                    VendorWhoop = table.Column<bool>(type: "boolean", nullable: false),
                    VendorRing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricDefinitions", x => x.MetricCode);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceRanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetricCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Population = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    MinNormal = table.Column<double>(type: "double precision", nullable: true),
                    MaxNormal = table.Column<double>(type: "double precision", nullable: true),
                    MinWarn = table.Column<double>(type: "double precision", nullable: true),
                    MaxWarn = table.Column<double>(type: "double precision", nullable: true),
                    Unit = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Source = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceRanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceEventTypeMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Entity = table.Column<string>(type: "text", nullable: true),
                    SourceEventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EventTypeCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Availability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventTypeMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValueDictionary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Column = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true),
                    WhenSet = table.Column<string>(type: "text", nullable: true),
                    Example = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValueDictionary", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorMetricDefinitions",
                columns: table => new
                {
                    VendorMetricCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    Grain = table.Column<string>(type: "text", nullable: true),
                    Episodes = table.Column<string>(type: "text", nullable: true),
                    ValueType = table.Column<string>(type: "text", nullable: true),
                    ScaleUnit = table.Column<string>(type: "text", nullable: true),
                    Vendor = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    VendorField = table.Column<string>(type: "text", nullable: true),
                    VendorMetricType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Direction = table.Column<string>(type: "text", nullable: true),
                    UsePolicy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ComparisonRule = table.Column<string>(type: "text", nullable: true),
                    FormulaTransparency = table.Column<string>(type: "text", nullable: true),
                    KnownInputs = table.Column<string>(type: "text", nullable: true),
                    VendorApi = table.Column<string>(type: "text", nullable: true),
                    AppleHealth = table.Column<string>(type: "text", nullable: true),
                    HealthConnect = table.Column<string>(type: "text", nullable: true),
                    AvailableInMvp = table.Column<bool>(type: "boolean", nullable: false),
                    Docs = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorMetricDefinitions", x => x.VendorMetricCode);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventTypeCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TimezoneOffset = table.Column<TimeSpan>(type: "interval", nullable: true),
                    DeviceInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceRecordId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SourceEventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SourceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceParentRecordId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_DeviceInstances_DeviceInstanceId",
                        column: x => x.DeviceInstanceId,
                        principalTable: "DeviceInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Events_EventTypeDefinitions_EventTypeCode",
                        column: x => x.EventTypeCode,
                        principalTable: "EventTypeDefinitions",
                        principalColumn: "EventTypeCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Events_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetricCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    ValueNum = table.Column<double>(type: "double precision", nullable: true),
                    ValueJson = table.Column<string>(type: "jsonb", nullable: true),
                    ValueSecondary = table.Column<double>(type: "double precision", nullable: true),
                    Unit = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TimezoneOffset = table.Column<TimeSpan>(type: "interval", nullable: true),
                    DeviceInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceRecordId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SourceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClientId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Observations_DeviceInstances_DeviceInstanceId",
                        column: x => x.DeviceInstanceId,
                        principalTable: "DeviceInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Observations_MetricDefinitions_MetricCode",
                        column: x => x.MetricCode,
                        principalTable: "MetricDefinitions",
                        principalColumn: "MetricCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorMetricCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    ValueNum = table.Column<double>(type: "double precision", nullable: true),
                    ValueText = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Unit = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceRecordId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SourceState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SourceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceDetails = table.Column<string>(type: "jsonb", nullable: true),
                    SourceDetailsSchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeviceInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorMetrics_DeviceInstances_DeviceInstanceId",
                        column: x => x.DeviceInstanceId,
                        principalTable: "DeviceInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VendorMetrics_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorMetrics_VendorMetricDefinitions_VendorMetricCode",
                        column: x => x.VendorMetricCode,
                        principalTable: "VendorMetricDefinitions",
                        principalColumn: "VendorMetricCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementEventLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeasurementId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeasurementType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkMethod = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementEventLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeasurementEventLinks_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DerivedMetrics_UserId_MetricCode_EffectiveAt",
                table: "DerivedMetrics",
                columns: new[] { "UserId", "MetricCode", "EffectiveAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceInstances_UserId_IntegrationPlatform_DataOriginAppId_~",
                table: "DeviceInstances",
                columns: new[] { "UserId", "IntegrationPlatform", "DataOriginAppId", "SourceDeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_DeviceInstanceId",
                table: "Events",
                column: "DeviceInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_EventTypeCode",
                table: "Events",
                column: "EventTypeCode");

            migrationBuilder.CreateIndex(
                name: "IX_Events_UserId_ClientId",
                table: "Events",
                columns: new[] { "UserId", "ClientId" },
                unique: true,
                filter: "\"ClientId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Events_UserId_EventTypeCode_StartAt",
                table: "Events",
                columns: new[] { "UserId", "EventTypeCode", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_UserId_StartAt",
                table: "Events",
                columns: new[] { "UserId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventTypeDefinitions_Mvp",
                table: "EventTypeDefinitions",
                column: "Mvp");

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementEventLinks_EventId_MeasurementId_MeasurementType",
                table: "MeasurementEventLinks",
                columns: new[] { "EventId", "MeasurementId", "MeasurementType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementEventLinks_MeasurementId_MeasurementType",
                table: "MeasurementEventLinks",
                columns: new[] { "MeasurementId", "MeasurementType" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_DeviceInstanceId",
                table: "Observations",
                column: "DeviceInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_MetricCode",
                table: "Observations",
                column: "MetricCode");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_UserId_ClientId",
                table: "Observations",
                columns: new[] { "UserId", "ClientId" },
                unique: true,
                filter: "\"ClientId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_UserId_MetricCode_StartAt",
                table: "Observations",
                columns: new[] { "UserId", "MetricCode", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceRanges_MetricCode_Population",
                table: "ReferenceRanges",
                columns: new[] { "MetricCode", "Population" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceEventTypeMaps_Source_SourceEventType",
                table: "SourceEventTypeMaps",
                columns: new[] { "Source", "SourceEventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ValueDictionary_Column_Value",
                table: "ValueDictionary",
                columns: new[] { "Column", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorMetrics_DeviceInstanceId",
                table: "VendorMetrics",
                column: "DeviceInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorMetrics_UserId_ClientId",
                table: "VendorMetrics",
                columns: new[] { "UserId", "ClientId" },
                unique: true,
                filter: "\"ClientId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VendorMetrics_UserId_VendorMetricCode_EffectiveAt",
                table: "VendorMetrics",
                columns: new[] { "UserId", "VendorMetricCode", "EffectiveAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorMetrics_VendorMetricCode",
                table: "VendorMetrics",
                column: "VendorMetricCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DerivedMetrics");

            migrationBuilder.DropTable(
                name: "MeasurementEventLinks");

            migrationBuilder.DropTable(
                name: "Observations");

            migrationBuilder.DropTable(
                name: "ReferenceRanges");

            migrationBuilder.DropTable(
                name: "SourceEventTypeMaps");

            migrationBuilder.DropTable(
                name: "ValueDictionary");

            migrationBuilder.DropTable(
                name: "VendorMetrics");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "MetricDefinitions");

            migrationBuilder.DropTable(
                name: "VendorMetricDefinitions");

            migrationBuilder.DropTable(
                name: "DeviceInstances");

            migrationBuilder.DropTable(
                name: "EventTypeDefinitions");
        }
    }
}

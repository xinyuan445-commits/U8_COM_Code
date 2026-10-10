using System;
using System.Collections.Generic;
using LiteDB;
using MaterialOutboundApi.Models;
using Newtonsoft.Json;

namespace MaterialOutboundApi.Services
{
    internal sealed class PreviewRecord
    {
        [BsonId]
        public string Token { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string WorkOrderCode { get; set; }
        public List<int> SelectedMoDIds { get; set; }
        public DateTime VoucherDate { get; set; }
        public string Json { get; set; }
    }

    internal sealed class IdempotencyRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string PayloadHash { get; set; }
        public string Status { get; set; }
        public string VoucherCode { get; set; }
        public string VoucherId { get; set; }
        public string ResultJson { get; set; }
        public string PayloadJson { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    internal sealed class StateStore : IDisposable
    {
        private readonly object _sync = new object();
        private readonly LiteDatabase _database;

        public StateStore(string path)
        {
            _database = new LiteDatabase(
                new ConnectionString
                {
                    Filename = path,
                    Connection = ConnectionType.Shared
                });
            _database.GetCollection<PreviewRecord>("previews")
                .EnsureIndex(x => x.ExpiresAt);
            _database.GetCollection<IdempotencyRecord>("idempotency")
                .EnsureIndex(x => x.UpdatedAt);
            _database.GetCollection<ShapeConversionPreviewRecord>("shapePreviews")
                .EnsureIndex(x => x.ExpiresAt);
        }

        public PreviewRecord SavePreview(PreviewDto preview, List<int> selected)
        {
            var record = new PreviewRecord
            {
                Token = preview.PreviewToken,
                ExpiresAt = preview.ExpiresAt,
                WorkOrderCode = preview.WorkOrderCode,
                SelectedMoDIds = selected,
                VoucherDate = preview.VoucherDate,
                Json = JsonConvert.SerializeObject(preview)
            };
            lock (_sync)
            {
                Cleanup();
                _database.GetCollection<PreviewRecord>("previews").Upsert(record);
            }
            return record;
        }

        public PreviewRecord GetPreview(string token)
        {
            lock (_sync)
            {
                PreviewRecord record = _database
                    .GetCollection<PreviewRecord>("previews")
                    .FindById(token);
                return record == null || record.ExpiresAt < DateTime.Now
                    ? null
                    : record;
            }
        }

        public IdempotencyRecord GetIdempotency(string id)
        {
            lock (_sync)
            {
                return _database
                    .GetCollection<IdempotencyRecord>("idempotency")
                    .FindById(id);
            }
        }

        public void UpsertIdempotency(IdempotencyRecord record)
        {
            record.UpdatedAt = DateTime.Now;
            lock (_sync)
            {
                _database
                    .GetCollection<IdempotencyRecord>("idempotency")
                    .Upsert(record);
            }
        }

        public void SaveShapePreview(ShapeConversionPreviewRecord record)
        {
            lock (_sync)
            {
                Cleanup();
                _database.GetCollection<ShapeConversionPreviewRecord>("shapePreviews")
                    .Upsert(record);
            }
        }

        public ShapeConversionPreviewRecord GetShapePreview(string token)
        {
            lock (_sync)
            {
                ShapeConversionPreviewRecord record = _database
                    .GetCollection<ShapeConversionPreviewRecord>("shapePreviews")
                    .FindById(token);
                return record == null || record.ExpiresAt < DateTime.Now
                    ? null
                    : record;
            }
        }

        private void Cleanup()
        {
            DateTime now = DateTime.Now;
            _database.GetCollection<PreviewRecord>("previews")
                .DeleteMany(x => x.ExpiresAt < now);
            _database.GetCollection<ShapeConversionPreviewRecord>("shapePreviews")
                .DeleteMany(x => x.ExpiresAt < now);
            DateTime cutoff = now.AddDays(-90);
            _database.GetCollection<IdempotencyRecord>("idempotency")
                .DeleteMany(x => x.UpdatedAt < cutoff);
        }

        public void Dispose()
        {
            _database?.Dispose();
        }
    }
}

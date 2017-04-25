﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapTileGenerator.Core
{
    public class TileGenerator : IDisposable
    {
        private ITileOutputStrategy _outputStrategy = null;
        private ITileLoadStrategy _tileLoadStrategy = new HttpTileLoadStrategy();
        private ISourceProvider _source = null;
        private FailTilesOutputStrategy _failsStrategy = new FailTilesOutputStrategy();
        private QueueTaskWorker<TileCoordWrap> _worker = null;
        private MapConfig _mapConfig = null;

        public EventHandler<TileCoord> TileLoaded;
        public EventHandler Finished;

        public TileGenerator(MapConfig config)
        {
            _mapConfig = config;
            _source = ProviderFactory.CreateSourceProvider(config);
            _outputStrategy = ProviderFactory.CreateOutputStrategy(config);
            _worker = new Core.QueueTaskWorker<TileCoordWrap>(config.RunThreadCount, GetTile, true);
            _totalTile = _source.TileGrid.TotalTile;
        }

        private int _successTileIndex = 0;
        private int _currTileIndex = 0;
        public int SuccessTileIndex
        {
            get
            {
                return _successTileIndex;
            }
        }

        private double _totalTile;
        public double TotalTile
        {
            get
            {
                return _totalTile;
            }
        }

        public int FailTiles
        {
            get
            {
                return _failsStrategy.Count;
            }
        }


        public ISourceProvider MapSource
        {
            get
            {
                return _source;
            }
        }

        public void Start()
        {
            if (_worker != null)
            {
                _worker.Start();
            }
            //重试失败的瓦片；
            TryDoFails();

            //从上次失败的瓦片开始下载瓦片
            _source.EnumerateTileRange(_mapConfig.LastTile,               
               (tile) =>
               {
                   _worker.TryQueue(new TileCoordWrap
                   {
                       Tile = tile,
                       OnSuccess = null,
                       OnFailed = (tile1,ex) =>
                       {
                           _failsStrategy.Insert(tile1);
                           Console.WriteLine(ex.Message);
                       },
                       OnFinally = (tile2) =>
                       {
                           _mapConfig.LastTile = tile2;
                           if (_currTileIndex == TotalTile)
                           {
                               _mapConfig.Save();
                               OnFinished();
                           }
                       }
                   });
               });
        }

        public void RetryFails()
        {
            _currTileIndex = 0;
            TryDoFails();
        }

        public void Close()
        {
            _mapConfig.Save();
            if (_worker != null)
            {
                _worker.Close();
            }
        }

        #region IDisposable 成员

        public void Dispose()
        {
            Close();
        }

        #endregion

        protected virtual void OnTileLoaded(TileCoord tileCoord)
        {
            Interlocked.Increment(ref _successTileIndex);
            if (this.TileLoaded != null)
            {
                TileLoaded(this, tileCoord);
            }
        }

        protected virtual void OnFinished()
        {
            this.Finished?.Invoke(this, EventArgs.Empty);
        }

        private void GetTile(TileCoordWrap tileWrap)
        {
            try
            {
                Interlocked.Increment(ref _currTileIndex);
                string url = _source.GetRequestUrl(tileWrap.Tile);
                using (Stream stream = _tileLoadStrategy.GetTile(url))
                {
                    _outputStrategy.Write(stream, _source.GetOutputTile(tileWrap.Tile, _mapConfig.OffsetZoom));
                    tileWrap.OnSuccess?.Invoke();
                    OnTileLoaded(tileWrap.Tile);
                }
            }
            catch (Exception ex)
            {
                tileWrap.OnFailed.Invoke(tileWrap.Tile, ex);
            }
            finally
            {
                tileWrap.OnFinally.Invoke(tileWrap.Tile);
            }
        }

        private void TryDoFails()
        {
            string failsDb = Path.Combine(_mapConfig.SavePath, FailTilesOutputStrategy.FILENAME);
            var failTiles = _failsStrategy.Load(failsDb);
            if (failTiles != null)
            {
                foreach (FailTileDto failTile in failTiles)
                {
                    _worker.TryQueue(new TileCoordWrap
                    {
                        Tile = failTile.ConvertTo(),
                        OnSuccess = () =>
                        {
                            _failsStrategy.Delete(failTile);
                        },
                        OnFailed = (tile,ex) =>
                        {
                            Console.WriteLine(ex.Message);
                        },
                        OnFinally = (tile) =>
                        {
                            if (_currTileIndex == FailTiles)
                            {
                                OnFinished();
                            }
                        }
                    });
                }
            }
        }
    }

    public class TileCoordWrap
    {
        public TileCoord Tile;
        public Action OnSuccess = null;
        public Action<TileCoord,Exception> OnFailed = null;
        public Action<TileCoord> OnFinally = null;
    }
}

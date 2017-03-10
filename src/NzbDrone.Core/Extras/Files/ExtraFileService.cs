﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Extras.Files
{
    public interface IExtraFileService<TExtraFile>
        where TExtraFile : ExtraFile, new()
    {
        List<TExtraFile> GetFilesBySeries(int seriesId);
        List<TExtraFile> GetFilesByEpisodeFile(int episodeFileId);

        List<TExtraFile> GetFilesByMovie(int movieId);

        TExtraFile FindByPath(string path);
        void Upsert(TExtraFile extraFile);
        void Upsert(List<TExtraFile> extraFiles);
        void Delete(int id);
        void DeleteMany(IEnumerable<int> ids);
    }

    public abstract class ExtraFileService<TExtraFile> : IExtraFileService<TExtraFile>,
                                                         IHandleAsync<MovieDeletedEvent>,
                                                         IHandleAsync<MovieFileDeletedEvent>
        where TExtraFile : ExtraFile, new()
    {
        private readonly IExtraFileRepository<TExtraFile> _repository;
        private readonly IMovieService _movieService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly Logger _logger;

        public ExtraFileService(IExtraFileRepository<TExtraFile> repository,
                                IMovieService movieService,
                                IDiskProvider diskProvider,
                                IRecycleBinProvider recycleBinProvider,
                                Logger logger)
        {
            _repository = repository;
            _movieService = movieService;
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _logger = logger;
        }

        public virtual bool PermanentlyDelete => false;

        public List<TExtraFile> GetFilesByMovie(int movieId)
        {
            return _repository.GetFilesByMovie(movieId);
        }

        public List<TExtraFile> GetFilesBySeries(int seriesId)
        {
            return _repository.GetFilesBySeries(seriesId);
        }

        public List<TExtraFile> GetFilesByEpisodeFile(int episodeFileId)
        {
            return _repository.GetFilesByEpisodeFile(episodeFileId);
        }

        public TExtraFile FindByPath(string path)
        {
            return _repository.FindByPath(path);
        }

        public void Upsert(TExtraFile extraFile)
        {
            Upsert(new List<TExtraFile> { extraFile });
        }

        public void Upsert(List<TExtraFile> extraFiles)
        {
            extraFiles.ForEach(m =>
            {
                m.LastUpdated = DateTime.UtcNow;

                if (m.Id == 0)
                {
                    m.Added = m.LastUpdated;
                }
            });

            _repository.InsertMany(extraFiles.Where(m => m.Id == 0).ToList());
            _repository.UpdateMany(extraFiles.Where(m => m.Id > 0).ToList());
        }

        public void Delete(int id)
        {
            _repository.Delete(id);
        }

        public void DeleteMany(IEnumerable<int> ids)
        {
            _repository.DeleteMany(ids);
        }

        public void HandleAsync(MovieDeletedEvent message)
        {
            _logger.Debug("Deleting Extra from database for movie: {0}", message.Movie);
            _repository.DeleteForMovie(message.Movie.Id);
        }

        public void HandleAsync(MovieFileDeletedEvent message)
        {
            var movieFile = message.MovieFile;

            if (message.Reason == DeleteMediaFileReason.NoLinkedEpisodes)
            {
                _logger.Debug("Removing episode file from DB as part of cleanup routine, not deleting extra files from disk.");
            }

            else
            {
                var movie = _movieService.GetMovie(movieFile.MovieId);

                foreach (var extra in _repository.GetFilesByMovieFile(movieFile.Id))
                {
                    var path = Path.Combine(movie.Path, extra.RelativePath);

                    if (_diskProvider.FileExists(path))
                    {
                        if (PermanentlyDelete)
                        {
                            _diskProvider.DeleteFile(path);
                        }

                        else
                        {
                            // Send extra files to the recycling bin so they can be recovered if necessary
                            _recycleBinProvider.DeleteFile(path);
                        }
                    }
                }
            }

            _logger.Debug("Deleting Extra from database for episode file: {0}", movieFile);
            _repository.DeleteForMovieFile(movieFile.Id);
        }
    }
}

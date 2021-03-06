﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Shoko.Commons.Queue;
using Shoko.Models.Enums;
using Shoko.Models.Queue;
using Shoko.Models.Server;
using Shoko.Server.Extensions;
using Shoko.Server.Models;
using Shoko.Server.Repositories;

namespace Shoko.Server.Commands
{
    [Serializable]
    [Command(CommandRequestType.AniDB_AddFileUDP)]
    public class CommandRequest_AddFileToMyList : CommandRequestImplementation
    {
        public string Hash { get; set; }
        public bool ReadStates { get; set; } = true;


        [NonSerialized]
        private SVR_VideoLocal vid;

        public override CommandRequestPriority DefaultPriority => CommandRequestPriority.Priority6;

        public override QueueStateStruct PrettyDescription
        {
            get
            {
                if (vid != null)
                    return new QueueStateStruct
                    {
                        queueState = QueueStateEnum.AniDB_MyListAdd,
                        extraParams = new[] {vid.FileName}
                    };
                return new QueueStateStruct
                {
                    queueState = QueueStateEnum.AniDB_MyListAdd,
                    extraParams = new[] {Hash}
                };
            }
        }

        public CommandRequest_AddFileToMyList()
        {
        }

        public CommandRequest_AddFileToMyList(string hash, bool readstate = true)
        {
            Hash = hash;
            ReadStates = readstate;
            Priority = (int) DefaultPriority;

            GenerateCommandID();
        }

        public override void ProcessCommand()
        {
            logger.Info($"Processing CommandRequest_AddFileToMyList: {vid?.FileName} - {Hash} - {ReadStates}");

            try
            {
                if (vid == null) return;

                // when adding a file via the API, newWatchedStatus will return with current watched status on AniDB
                // if the file is already on the user's list

                bool isManualLink = false;
                List<CrossRef_File_Episode> xrefs = vid.EpisodeCrossRefs;
                if (xrefs.Count > 0)
                    isManualLink = xrefs[0].CrossRefSource != (int) CrossRefSource.AniDB;

                // mark the video file as watched
                List<SVR_JMMUser> aniDBUsers = RepoFactory.JMMUser.GetAniDBUsers();
                SVR_JMMUser juser = aniDBUsers.FirstOrDefault();
                DateTime? originalWatchedDate = null;
                if (juser != null)
                    originalWatchedDate = vid.GetUserRecord(juser.JMMUserID)?.WatchedDate;

                DateTime? newWatchedDate;
                int? lid;
                AniDBFile_State? state = null;

                if (isManualLink)
                    (lid, newWatchedDate) = ShokoService.AnidbProcessor.AddFileToMyList(xrefs[0].AnimeID,
                        xrefs[0].GetEpisode().EpisodeNumber, originalWatchedDate);
                else
                    (lid, newWatchedDate) =
                        ShokoService.AnidbProcessor.AddFileToMyList(vid, originalWatchedDate, ref state);

                if (lid != null && lid.Value > 0)
                {
                    vid.MyListID = lid.Value;
                    RepoFactory.VideoLocal.Save(vid);
                }

                if (juser != null)
                {
                    string datemessage = newWatchedDate?.ToShortDateString() ?? "Not Watched";
                    if (newWatchedDate?.Equals(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime()) ?? false)
                        datemessage = "No Watch Date Specified";
                    logger.Info($"Adding file to list: {vid.FileName} - {datemessage}");
                    bool watched = newWatchedDate != null;

                    bool watchedLocally = originalWatchedDate != null;
                    bool watchedChanged = watched != watchedLocally;

                    if (ReadStates)
                    {
                        // handle import watched settings. Don't update AniDB in either case, we'll do that with the storage state
                        if (ServerSettings.AniDB_MyList_ReadWatched && watched && !watchedLocally)
                        {
                            vid.ToggleWatchedStatus(true, false, newWatchedDate, false, juser.JMMUserID,
                                false, false);
                        }
                        else if (ServerSettings.AniDB_MyList_ReadUnwatched && !watched && watchedLocally)
                        {
                            vid.ToggleWatchedStatus(false, false, null, false, juser.JMMUserID,
                                false, false);
                        }
                    }

                    // We should have a MyListID at this point, so hopefully this will prevent looping
                    if (vid.MyListID > 0 && (watchedChanged || state != ServerSettings.AniDB_MyList_StorageState))
                    {
                        if (ServerSettings.AniDB_MyList_SetWatched && watchedLocally)
                            ShokoService.AnidbProcessor.UpdateMyListFileStatus(vid, true, originalWatchedDate);
                        else if (ServerSettings.AniDB_MyList_SetUnwatched && !watchedLocally)
                            ShokoService.AnidbProcessor.UpdateMyListFileStatus(vid, false);
                    }
                }

                // if we don't have xrefs, then no series or eps.
                if (xrefs.Count <= 0) return;

                SVR_AnimeSeries ser = RepoFactory.AnimeSeries.GetByAnimeID(xrefs[0].AnimeID);
                // all the eps should belong to the same anime
                ser.QueueUpdateStats();
                //StatsCache.Instance.UpdateUsingSeries(ser.AnimeSeriesID);

                // lets also try adding to the users trakt collecion
                if (ServerSettings.Trakt_IsEnabled &&
                    !string.IsNullOrEmpty(ServerSettings.Trakt_AuthToken))
                {
                    foreach (SVR_AnimeEpisode aep in vid.GetAnimeEpisodes())
                    {
                        CommandRequest_TraktCollectionEpisode cmdSyncTrakt =
                            new CommandRequest_TraktCollectionEpisode(aep.AnimeEpisodeID, TraktSyncAction.Add);
                        cmdSyncTrakt.Save();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Error processing CommandRequest_AddFileToMyList: {Hash} - {ex}");
            }
        }

        /// <summary>
        /// This should generate a unique key for a command
        /// It will be used to check whether the command has already been queued before adding it
        /// </summary>
        public override void GenerateCommandID()
        {
            CommandID = $"CommandRequest_AddFileToMyList_{Hash}";
        }

        public override bool LoadFromDBCommand(CommandRequest cq)
        {
            CommandID = cq.CommandID;
            CommandRequestID = cq.CommandRequestID;
            Priority = cq.Priority;
            CommandDetails = cq.CommandDetails;
            DateTimeUpdated = cq.DateTimeUpdated;

            // read xml to get parameters
            if (CommandDetails.Trim().Length > 0)
            {
                XmlDocument docCreator = new XmlDocument();
                docCreator.LoadXml(CommandDetails);

                // populate the fields
                Hash = TryGetProperty(docCreator, "CommandRequest_AddFileToMyList", "Hash");
                string read = TryGetProperty(docCreator, "CommandRequest_AddFileToMyList", "ReadStates");
                if (!bool.TryParse(read, out bool read_states)) read_states = true;
                ReadStates = read_states;
            }

            if (Hash.Trim().Length <= 0) return false;
            vid = RepoFactory.VideoLocal.GetByHash(Hash);
            return true;
        }

        public override CommandRequest ToDatabaseObject()
        {
            GenerateCommandID();

            CommandRequest cq = new CommandRequest
            {
                CommandID = CommandID,
                CommandType = CommandType,
                Priority = Priority,
                CommandDetails = ToXML(),
                DateTimeUpdated = DateTime.Now
            };
            return cq;
        }
    }
}

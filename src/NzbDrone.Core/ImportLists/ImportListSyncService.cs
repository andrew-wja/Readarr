using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListSyncService : IExecute<ImportListSyncCommand>
    {
        private readonly IImportListFactory _importListFactory;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly IFetchAndParseImportList _listFetcherAndParser;
        private readonly IGoodreadsProxy _goodreadsProxy;
        private readonly IGoodreadsSearchProxy _goodreadsSearchProxy;
        private readonly IProvideBookInfo _bookInfoProxy;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IAddAuthorService _addAuthorService;
        private readonly IAddBookService _addBookService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public ImportListSyncService(IImportListFactory importListFactory,
                                     IImportListExclusionService importListExclusionService,
                                     IFetchAndParseImportList listFetcherAndParser,
                                     IGoodreadsProxy goodreadsProxy,
                                     IGoodreadsSearchProxy goodreadsSearchProxy,
                                     IProvideBookInfo bookInfoProxy,
                                     IAuthorService authorService,
                                     IBookService bookService,
                                     IEditionService editionService,
                                     IAddAuthorService addAuthorService,
                                     IAddBookService addBookService,
                                     IEventAggregator eventAggregator,
                                     IManageCommandQueue commandQueueManager,
                                     Logger logger)
        {
            _importListFactory = importListFactory;
            _importListExclusionService = importListExclusionService;
            _listFetcherAndParser = listFetcherAndParser;
            _goodreadsProxy = goodreadsProxy;
            _goodreadsSearchProxy = goodreadsSearchProxy;
            _bookInfoProxy = bookInfoProxy;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _addAuthorService = addAuthorService;
            _addBookService = addBookService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        private List<Book> SyncAll()
        {
            if (_importListFactory.AutomaticAddEnabled().Empty())
            {
                _logger.Debug("No import lists with automatic add enabled");

                return new List<Book>();
            }

            _logger.ProgressInfo("Starting Import List Sync");

            var listItems = _listFetcherAndParser.Fetch().ToList();

            return ProcessListItems(listItems);
        }

        private List<Book> SyncList(ImportListDefinition definition)
        {
            _logger.ProgressInfo($"Starting Import List Refresh for List {definition.Name}");

            var listItems = _listFetcherAndParser.FetchSingleList(definition).ToList();

            return ProcessListItems(listItems);
        }

        private List<Book> ProcessListItems(List<ImportListItemInfo> items)
        {
            var processed = new List<Book>();
            var authorsToAdd = new List<Author>();
            var booksToAdd = new List<Book>();

            if (items.Count == 0)
            {
                _logger.ProgressInfo("No list items to process");

                return new List<Book>();
            }

            _logger.ProgressInfo("Processing {0} list items", items.Count);

            var reportNumber = 1;

            var listExclusions = _importListExclusionService.All();

            foreach (var report in items)
            {
                _logger.ProgressTrace("Processing list item {0}/{1}", reportNumber, items.Count);

                reportNumber++;

                var importList = _importListFactory.Get(report.ImportListId);

                if (report.Book.IsNotNullOrWhiteSpace() || report.EditionForeignId.IsNotNullOrWhiteSpace())
                {
                    if (report.EditionForeignId.IsNullOrWhiteSpace() || report.AuthorForeignId.IsNullOrWhiteSpace() || report.BookForeignId.IsNullOrWhiteSpace())
                    {
                        MapBookReport(report);
                    }

                    ProcessBookReport(importList, report, listExclusions, booksToAdd, authorsToAdd);
                }
                else if (report.Author.IsNotNullOrWhiteSpace() || report.AuthorForeignId.IsNotNullOrWhiteSpace())
                {
                    if (report.AuthorForeignId.IsNullOrWhiteSpace())
                    {
                        MapAuthorReport(report);
                    }

                    ProcessAuthorReport(importList, report, listExclusions, authorsToAdd);
                }
            }

            var addedAuthors = _addAuthorService.AddAuthors(authorsToAdd, false);
            var addedBooks = _addBookService.AddBooks(booksToAdd, false);

            var message = string.Format($"Import List Sync Completed. Items found: {items.Count}, Authors added: {authorsToAdd.Count}, Books added: {booksToAdd.Count}");

            _logger.ProgressInfo(message);

            var toRefresh = addedAuthors.Select(x => x.Id).Concat(addedBooks.Select(x => x.Author.Value.Id)).Distinct().ToList();
            if (toRefresh.Any())
            {
                _commandQueueManager.Push(new BulkRefreshAuthorCommand(toRefresh, true));
            }

            return processed;
        }

        private void MapBookReport(ImportListItemInfo report)
        {
            if (report.AuthorForeignId.IsNotNullOrWhiteSpace() && report.BookForeignId.IsNotNullOrWhiteSpace())
            {
                return;
            }

            if (report.EditionForeignId.IsNotNullOrWhiteSpace() && int.TryParse(report.EditionForeignId, out var goodreadsId))
            {
                // check the local DB
                var edition = _editionService.GetEditionByForeignEditionId(report.EditionForeignId);

                if (edition != null)
                {
                    var book = edition.Book.Value;
                    report.BookForeignId = book.ForeignBookId;
                    report.Book = edition.Title;
                    report.Author ??= book.AuthorMetadata.Value.Name;
                    report.AuthorForeignId ??= book.AuthorMetadata.Value.ForeignAuthorId;
                    return;
                }

                try
                {
                    var remoteBook = _goodreadsProxy.GetBookInfo(report.EditionForeignId);

                    _logger.Trace($"Mapped {report.EditionForeignId} to [{remoteBook.ForeignBookId}] {remoteBook.Title}");

                    report.BookForeignId = remoteBook.ForeignBookId;
                    report.Book = remoteBook.Title;
                    report.Author ??= remoteBook.AuthorMetadata.Value.Name;
                    report.AuthorForeignId ??= remoteBook.AuthorMetadata.Value.ForeignAuthorId;
                }
                catch (BookNotFoundException)
                {
                    _logger.Debug($"Nothing found for edition [{report.EditionForeignId}]");
                    report.EditionForeignId = null;
                }
            }
            else if (report.BookForeignId.IsNotNullOrWhiteSpace())
            {
                var mappedBook = _bookInfoProxy.GetBookInfo(report.BookForeignId);

                report.BookForeignId = mappedBook.Item2.ForeignBookId;
                report.Book = mappedBook.Item2.Title;
                report.AuthorForeignId = mappedBook.Item3.First().ForeignAuthorId;
            }
            else
            {
                var mappedBook = _goodreadsSearchProxy.Search($"{report.Book} {report.Author}").FirstOrDefault();

                if (mappedBook == null)
                {
                    _logger.Trace($"Nothing found for {report.Author} - {report.Book}");
                    return;
                }

                _logger.Trace($"Mapped Book {report.Book} by Author {report.Author} to [{mappedBook.WorkId}] {mappedBook.BookTitleBare}");

                report.BookForeignId = mappedBook.WorkId.ToString();
                report.Book = mappedBook.BookTitleBare;
                report.Author ??= mappedBook.Author.Name;
                report.AuthorForeignId ??= mappedBook.Author.Id.ToString();
                report.EditionForeignId = mappedBook.BookId.ToString();
            }
        }

        private void ProcessBookReport(ImportListDefinition importList, ImportListItemInfo report, List<ImportListExclusion> listExclusions, List<Book> booksToAdd, List<Author> authorsToAdd)
        {
            // Check to see if book in DB
            var existingBook = _bookService.FindById(report.BookForeignId);

            // Check to see if book excluded
            var excludedBook = listExclusions.SingleOrDefault(s => s.ForeignId == report.BookForeignId);

            // Check to see if author excluded
            var excludedAuthor = listExclusions.SingleOrDefault(s => s.ForeignId == report.AuthorForeignId);

            if (excludedBook != null)
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion", report.EditionForeignId, report.Book);
                return;
            }

            if (excludedAuthor != null)
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion for parent author", report.EditionForeignId, report.Book);
                return;
            }

            if (existingBook != null)
            {
                _logger.Debug("{0} [{1}] Rejected, Book Exists in DB.  Ensuring Book and Author monitored.", report.EditionForeignId, report.Book);

                if (importList.ShouldMonitorExisting && importList.ShouldMonitor != ImportListMonitorType.None)
                {
                    if (!existingBook.Monitored)
                    {
                        _bookService.SetBookMonitored(existingBook.Id, true);

                        if (importList.ShouldMonitor == ImportListMonitorType.SpecificBook)
                        {
                            _commandQueueManager.Push(new BookSearchCommand(new List<int> { existingBook.Id }));
                        }
                    }

                    var existingAuthor = existingBook.Author.Value;
                    var doSearch = false;

                    if (importList.ShouldMonitor == ImportListMonitorType.EntireAuthor)
                    {
                        if (existingAuthor.Books.Value.Any(x => !x.Monitored))
                        {
                            doSearch = true;
                            _bookService.SetMonitored(existingAuthor.Books.Value.Select(x => x.Id), true);
                        }
                    }

                    if (!existingAuthor.Monitored)
                    {
                        doSearch = true;
                        existingAuthor.Monitored = true;
                        _authorService.UpdateAuthor(existingAuthor);
                    }

                    if (doSearch)
                    {
                        _commandQueueManager.Push(new MissingBookSearchCommand(existingAuthor.Id));
                    }
                }

                return;
            }

            // Append Book if not already in DB or already on add list
            if (booksToAdd.All(s => s.ForeignBookId != report.BookForeignId))
            {
                var monitored = importList.ShouldMonitor != ImportListMonitorType.None;

                var toAddAuthor = new Author
                {
                    Monitored = monitored,
                    MonitorNewItems = importList.MonitorNewItems,
                    RootFolderPath = importList.RootFolderPath,
                    QualityProfileId = importList.ProfileId,
                    MetadataProfileId = importList.MetadataProfileId,
                    Tags = importList.Tags,
                    AddOptions = new AddAuthorOptions
                    {
                        SearchForMissingBooks = importList.ShouldSearch,
                        Monitored = monitored,
                        Monitor = monitored ? MonitorTypes.All : MonitorTypes.None
                    }
                };

                if (report.AuthorForeignId != null && report.Author != null)
                {
                    toAddAuthor = ProcessAuthorReport(importList, report, listExclusions, authorsToAdd);
                }

                var toAdd = new Book
                {
                    ForeignBookId = report.BookForeignId,
                    Monitored = monitored,
                    AnyEditionOk = true,
                    Editions = new List<Edition>(),
                    Author = toAddAuthor,
                    AddOptions = new AddBookOptions
                    {
                        // Only search for new book for existing authors
                        // New author searches are triggered by SearchForMissingBooks
                        SearchForNewBook = importList.ShouldSearch && toAddAuthor.Id > 0
                    }
                };

                if (report.EditionForeignId.IsNotNullOrWhiteSpace() && int.TryParse(report.EditionForeignId, out var goodreadsId))
                {
                    toAdd.Editions.Value.Add(new Edition
                    {
                        ForeignEditionId = report.EditionForeignId,
                        Monitored = true
                    });
                }

                if (importList.ShouldMonitor == ImportListMonitorType.SpecificBook && toAddAuthor.AddOptions != null)
                {
                    Debug.Assert(toAddAuthor.Id == 0, "new author added but ID is not 0");
                    toAddAuthor.AddOptions.BooksToMonitor.Add(toAdd.ForeignBookId);
                }

                booksToAdd.Add(toAdd);
            }
        }

        private void MapAuthorReport(ImportListItemInfo report)
        {
            var mappedBook = _goodreadsSearchProxy.Search(report.Author).FirstOrDefault();

            if (mappedBook == null)
            {
                _logger.Trace($"Nothing found for {report.Author}");
                return;
            }

            _logger.Trace($"Mapped {report.Author} to [{mappedBook.Author.Name}]");

            report.Author = mappedBook.Author.Name;
            report.AuthorForeignId = mappedBook.Author.Id.ToString();
        }

        private Author ProcessAuthorReport(ImportListDefinition importList, ImportListItemInfo report, List<ImportListExclusion> listExclusions, List<Author> authorsToAdd)
        {
            if (report.AuthorForeignId == null)
            {
                return null;
            }

            // Check to see if author in DB
            var existingAuthor = _authorService.FindById(report.AuthorForeignId);

            // Check to see if author excluded
            var excludedAuthor = listExclusions.SingleOrDefault(s => s.ForeignId == report.AuthorForeignId);

            // Check to see if author in import
            var existingImportAuthor = authorsToAdd.Find(i => i.ForeignAuthorId == report.AuthorForeignId);

            if (excludedAuthor != null)
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion", report.AuthorForeignId, report.Author);
                return null;
            }

            if (existingAuthor != null)
            {
                _logger.Debug("{0} [{1}] Rejected, Author Exists in DB.  Ensuring Author monitored", report.AuthorForeignId, report.Author);

                if (importList.ShouldMonitorExisting && !existingAuthor.Monitored)
                {
                    existingAuthor.Monitored = true;
                    _authorService.UpdateAuthor(existingAuthor);
                }

                return existingAuthor;
            }

            if (existingImportAuthor != null)
            {
                _logger.Debug("{0} [{1}] Rejected, Author Exists in Import.", report.AuthorForeignId, report.Author);

                return existingImportAuthor;
            }

            var monitored = importList.ShouldMonitor != ImportListMonitorType.None;

            var toAdd = new Author
            {
                Metadata = new AuthorMetadata
                {
                    ForeignAuthorId = report.AuthorForeignId,
                    Name = report.Author
                },
                Monitored = monitored,
                MonitorNewItems = importList.MonitorNewItems,
                RootFolderPath = importList.RootFolderPath,
                QualityProfileId = importList.ProfileId,
                MetadataProfileId = importList.MetadataProfileId,
                Tags = importList.Tags,
                AddOptions = new AddAuthorOptions
                {
                    SearchForMissingBooks = importList.ShouldSearch,
                    Monitored = monitored,
                    Monitor = monitored ? MonitorTypes.All : MonitorTypes.None
                }
            };

            authorsToAdd.Add(toAdd);

            return toAdd;
        }

        public void Execute(ImportListSyncCommand message)
        {
            var processed = message.DefinitionId.HasValue ? SyncList(_importListFactory.Get(message.DefinitionId.Value)) : SyncAll();

            _eventAggregator.PublishEvent(new ImportListSyncCompleteEvent(processed));
        }
    }
}

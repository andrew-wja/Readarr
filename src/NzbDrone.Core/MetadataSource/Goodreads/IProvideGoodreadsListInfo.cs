using NzbDrone.Core.MetadataSource.Goodreads;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideGoodreadsListInfo
    {
        ListResource GetListInfo(int id, int page, bool useCache = true);
    }
}

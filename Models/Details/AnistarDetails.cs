// Models/Details/AnistarDetails.cs

namespace JacRed.Models.Details
{
    /// <summary>
    /// Детали для AniStar — отдельное поле под ссылку скачивания .torrent.
    /// </summary>
    public class AnistarDetails : TorrentDetails
    {
        public string downloadUri { get; set; }
    }
}
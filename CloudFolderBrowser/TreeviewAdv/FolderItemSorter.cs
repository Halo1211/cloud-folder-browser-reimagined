using Aga.Controls.Tree;
using System;
using System.Collections;
using System.Windows.Forms;

namespace CloudFolderBrowser
{
	public class FolderItemSorter : IComparer
	{
		private readonly string _mode;
		private readonly SortOrder _order;

		public FolderItemSorter(string mode, SortOrder order)
		{
			_mode = mode;
			_order = order;
		}

		public int Compare(object? x, object? y)
		{
            if (ReferenceEquals(x, y))
                return 0;
            if (x is not ColumnNode a)
                return y is ColumnNode ? -1 : 0;
            if (y is not ColumnNode b)
                return 1;

            bool aIsFolder = a.Tag == null || a.Tag is IFolder;
            bool bIsFolder = b.Tag == null || b.Tag is IFolder;
            if (aIsFolder && !bIsFolder)
                return -1;
            if (bIsFolder && !aIsFolder)
                return 1;

            int res = 0;

            if (_mode == "Created")
                res = DateTime.Compare(a.CreatedTime, b.CreatedTime);
            if (_mode == "Modified")
            {
                res = DateTime.Compare(a.ModifiedTime, b.ModifiedTime);
            }
            if (_mode == "Size")
            {
                res = a.Size.CompareTo(b.Size);
            }
            if (_mode == "Name")
                res = string.Compare(a.NodeControl1, b.NodeControl1, StringComparison.CurrentCultureIgnoreCase);

			if (_order == SortOrder.Ascending)
				return res;
			return -res;
		}
	}
}

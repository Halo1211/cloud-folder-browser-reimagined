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
                return -1;
            if (y is not ColumnNode b)
                return 1;

            if ((a.Tag == null || a.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder") && (b.Tag != null && b.Tag.GetType().ToString() != "CloudFolderBrowser.CloudFolder"))
                return -1;
            if ((b.Tag == null || b.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder") && (a.Tag != null && a.Tag.GetType().ToString() != "CloudFolderBrowser.CloudFolder"))
                return 1;

            int res = 0;

			if (_mode == "Created"
                && DateTime.TryParse(a.NodeControl2, out DateTime createdA)
                && DateTime.TryParse(b.NodeControl2, out DateTime createdB))
				res = DateTime.Compare(createdA, createdB);
            if (_mode == "Modified")
            {
                if (DateTime.TryParse(a.NodeControl3, out DateTime modifiedA)
                    && DateTime.TryParse(b.NodeControl3, out DateTime modifiedB))
                    res = DateTime.Compare(modifiedA, modifiedB);
            }
            if (_mode == "Size")
            {
                double.TryParse(a.NodeControl4.Replace(" MB", ""), out double sizeA);
                double.TryParse(b.NodeControl4.Replace(" MB", ""), out double sizeB);
                res = sizeA.CompareTo(sizeB);
            }
            if (_mode == "Name")
                res = string.Compare(a.NodeControl1, b.NodeControl1, StringComparison.CurrentCultureIgnoreCase);

			if (_order == SortOrder.Ascending)
				return res;
			return -res;
		}
	}
}

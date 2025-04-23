using System.Windows.Forms;

namespace Captura.Models
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class DialogService : IDialogService
    {
        public string PickFolder(string Current, string Description)
        {
            using (var dlg = new FolderBrowserDialog
            {
                SelectedPath = Current,
                Description = Description
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    return dlg.SelectedPath;
            }

            return null;
        }

        public string PickFile(string InitialFolder, string Description)
        {
            var ofd = new OpenFileDialog
            {
                CheckFileExists = true,
                CheckPathExists = true,
                InitialDirectory = InitialFolder,
                Title = Description
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                return ofd.FileName;
            }

            return null;
        }
    }
}
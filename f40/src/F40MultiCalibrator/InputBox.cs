using System.Windows.Forms;

namespace F40MultiCalibrator;

public sealed class InputBox : Form
{
	private readonly TextBox _text = new TextBox
	{
		Dock = DockStyle.Top
	};

	public string Value => _text.Text;

	public InputBox(string prompt, string defaultValue)
	{
		Text = prompt;
		base.Width = 420;
		base.Height = 135;
		base.StartPosition = FormStartPosition.CenterParent;
		base.Controls.Add(_text);
		base.Controls.Add(new Label
		{
			Text = prompt,
			Dock = DockStyle.Top,
			Height = 30
		});
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			Height = 38,
			FlowDirection = FlowDirection.RightToLeft
		};
		Button button = new Button
		{
			Text = "确定",
			DialogResult = DialogResult.OK
		};
		Button button2 = new Button
		{
			Text = "取消",
			DialogResult = DialogResult.Cancel
		};
		flowLayoutPanel.Controls.Add(button);
		flowLayoutPanel.Controls.Add(button2);
		base.Controls.Add(flowLayoutPanel);
		base.AcceptButton = button;
		base.CancelButton = button2;
		_text.Text = defaultValue;
	}
}

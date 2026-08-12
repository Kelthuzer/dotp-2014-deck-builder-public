using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Be.Timvw.Framework.ComponentModel;
using RSN.Tools;

namespace RSN.DotP
{
	public partial class DeckBuilder
	{
		private readonly Dictionary<CardInfo, string> m_dicQuickSearch = new Dictionary<CardInfo, string>();
		private readonly Dictionary<string, CardInfo> m_dicForgeCardsByName = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, CardInfo> m_dicForgeCardsByNameAndExpansion = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
		private SortableBindingList<CardInfo> m_lstBaseFilteredCards;
		private ToolStripTextBox m_txtQuickSearch;
		private ToolStripLabel m_lblQuickSearch;
		private ToolStripLabel m_lblQuickSearchResults;
		private ToolStripButton m_btnClearQuickSearch;
		private Timer m_tmrQuickSearch;
		private Timer m_tmrPreviewHover;
		private CardInfo m_ciPendingPreview;
		private bool m_bQuickSearchPlaceholder;
		private Point m_ptDragStart;
		private DataGridView m_dgvDragSource;
		private DataGridView m_dgvDropTarget;
		private int m_nDropTargetIndex = -1;

		private static readonly Color ModernBackground = SystemColors.Control;
		private static readonly Color ModernSurface = SystemColors.Window;
		private static readonly Color ModernForeground = Color.Black;
		private static readonly Color ModernAccent = Color.FromArgb(204, 228, 247);

		private sealed class CardDragItem
		{
			public CardInfo Card;
			public DeckCard DeckCard;
		}

		private sealed class CardDragPayload
		{
			public readonly List<CardDragItem> Items = new List<CardDragItem>();
			public DeckLocation Source;
		}

		private sealed class ModernToolStripRenderer : ToolStripProfessionalRenderer
		{
			private static readonly Color EnabledText = Color.Black;
			private static readonly Color DisabledText = Color.Black;

			public ModernToolStripRenderer()
				: base(new ModernColourTable())
			{
			}

			protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
			{
				e.TextColor = e.Item.Enabled ? EnabledText : DisabledText;
				base.OnRenderItemText(e);
			}
		}

		private sealed class ModernColourTable : ProfessionalColorTable
		{
			private static readonly Color Surface = SystemColors.Control;
			private static readonly Color DropDownSurface = SystemColors.Window;
			private static readonly Color Accent = Color.FromArgb(204, 228, 247);

			public override Color MenuStripGradientBegin { get { return Surface; } }
			public override Color MenuStripGradientEnd { get { return Surface; } }
			public override Color ToolStripDropDownBackground { get { return DropDownSurface; } }
			public override Color ImageMarginGradientBegin { get { return DropDownSurface; } }
			public override Color ImageMarginGradientMiddle { get { return DropDownSurface; } }
			public override Color ImageMarginGradientEnd { get { return DropDownSurface; } }
			public override Color MenuItemSelected { get { return Accent; } }
			public override Color MenuItemBorder { get { return Accent; } }
			public override Color MenuItemPressedGradientBegin { get { return Accent; } }
			public override Color MenuItemPressedGradientEnd { get { return Accent; } }
			public override Color ToolStripBorder { get { return SystemColors.ControlDark; } }
			public override Color StatusStripGradientBegin { get { return Surface; } }
			public override Color StatusStripGradientEnd { get { return Surface; } }
		}

		private void InitializeModernInterface()
		{
			KeyPreview = true;
			KeyDown += DeckBuilder_ModernKeyDown;
			FormClosed += DeckBuilder_ModernFormClosed;
			MinimumSize = new Size(900, 650);

			m_tmrQuickSearch = new Timer();
			m_tmrQuickSearch.Interval = 180;
			m_tmrQuickSearch.Tick += QuickSearchTimer_Tick;
			m_tmrPreviewHover = new Timer();
			m_tmrPreviewHover.Interval = 90;
			m_tmrPreviewHover.Tick += PreviewHoverTimer_Tick;

			m_btnClearQuickSearch = new ToolStripButton();
			m_btnClearQuickSearch.Name = "btnClearQuickSearch";
			m_btnClearQuickSearch.Alignment = ToolStripItemAlignment.Right;
			m_btnClearQuickSearch.AutoSize = false;
			m_btnClearQuickSearch.Width = 26;
			m_btnClearQuickSearch.Text = "\u00D7";
			m_btnClearQuickSearch.DisplayStyle = ToolStripItemDisplayStyle.Text;
			m_btnClearQuickSearch.Enabled = false;
			m_btnClearQuickSearch.Click += ClearQuickSearch_Click;
			mnuMain.Items.Add(m_btnClearQuickSearch);

			m_txtQuickSearch = new ToolStripTextBox();
			m_txtQuickSearch.Name = "txtQuickSearch";
			m_txtQuickSearch.Alignment = ToolStripItemAlignment.Right;
			m_txtQuickSearch.AutoSize = false;
			m_txtQuickSearch.Width = 230;
			m_txtQuickSearch.BorderStyle = BorderStyle.FixedSingle;
			m_txtQuickSearch.TextChanged += QuickSearch_TextChanged;
			m_txtQuickSearch.Enter += QuickSearch_Enter;
			m_txtQuickSearch.Leave += QuickSearch_Leave;
			mnuMain.Items.Add(m_txtQuickSearch);

			m_lblQuickSearch = new ToolStripLabel();
			m_lblQuickSearch.Alignment = ToolStripItemAlignment.Right;
			m_lblQuickSearch.ForeColor = ModernForeground;
			mnuMain.Items.Add(m_lblQuickSearch);

			m_lblQuickSearchResults = new ToolStripLabel();
			m_lblQuickSearchResults.Alignment = ToolStripItemAlignment.Right;
			m_lblQuickSearchResults.ForeColor = ModernForeground;
			mnuMain.Items.Add(m_lblQuickSearchResults);
			RefreshModernLocalizedStrings();
			SetQuickSearchPlaceholder();

			ApplyModernTheme(this);
			ApplyModernTheme(cmnuContext);
			ApplyModernTheme(cmnuPictures);
			ImproveCompactButtonLayout();
			m_txtQuickSearch.BackColor = ModernSurface;
			m_txtQuickSearch.ForeColor = ModernForeground;
			m_lblQuickSearchResults.ForeColor = ModernForeground;
			ConfigureDragAndDrop(dgvCards);
			ConfigureDragAndDrop(dgvDeckCards);
			ConfigureDragAndDrop(dgvUnlocksRegular);
			ConfigureDragAndDrop(dgvUnlocksPromo);
		}

		private void ApplyModernTheme(Control root)
		{
			root.BackColor = ModernBackground;
			root.ForeColor = ModernForeground;

			DataGridView grid = root as DataGridView;
			if (grid != null)
			{
				grid.BackgroundColor = SystemColors.AppWorkspace;
				grid.BorderStyle = BorderStyle.FixedSingle;
				grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
				grid.GridColor = SystemColors.ControlLight;
				grid.EnableHeadersVisualStyles = false;
				grid.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
				grid.ColumnHeadersDefaultCellStyle.ForeColor = ModernForeground;
				grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SystemColors.Control;
				grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = ModernForeground;
				grid.ColumnHeadersHeight = 28;
				grid.DefaultCellStyle.BackColor = ModernSurface;
				grid.DefaultCellStyle.ForeColor = ModernForeground;
				grid.DefaultCellStyle.SelectionBackColor = ModernAccent;
				grid.DefaultCellStyle.SelectionForeColor = ModernForeground;
				grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 248, 248);
				grid.AlternatingRowsDefaultCellStyle.ForeColor = ModernForeground;
				grid.RowTemplate.Height = 26;
				grid.MultiSelect = true;
				grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
			}

			Button button = root as Button;
			if (button != null)
			{
				button.FlatStyle = FlatStyle.Flat;
				button.FlatAppearance.BorderColor = SystemColors.ControlDark;
				button.FlatAppearance.MouseOverBackColor = ModernAccent;
				button.UseVisualStyleBackColor = false;
				button.BackColor = ModernSurface;
				button.Padding = new Padding(4, 0, 4, 0);
				button.Paint -= ModernButton_Paint;
				button.Paint += ModernButton_Paint;
			}

			ToolStrip strip = root as ToolStrip;
			if (strip != null)
			{
				strip.BackColor = SystemColors.Control;
				strip.ForeColor = ModernForeground;
				strip.Renderer = new ModernToolStripRenderer();
				foreach (ToolStripItem item in strip.Items)
					item.ForeColor = ModernForeground;
			}

			TextBoxBase textBox = root as TextBoxBase;
			if (textBox != null)
			{
				textBox.BackColor = ModernSurface;
				textBox.ForeColor = ModernForeground;
				textBox.BorderStyle = BorderStyle.FixedSingle;
			}

			foreach (Control child in root.Controls)
				ApplyModernTheme(child);
		}

		private void ImproveCompactButtonLayout()
		{
			int buttonHeight = Math.Max(28, Font.Height + 12);
			ResizeBottomButtonRow(cmdDeckInformation, gbDeck, dgvDeckCards, buttonHeight);
			ResizeBottomButtonRow(scRegularButtons, gbRegularUnlocks, dgvUnlocksRegular, buttonHeight);
			ResizeBottomButtonRow(scPromoButtons, gbPromoUnlocks, dgvUnlocksPromo, buttonHeight);
		}

		private static void ResizeBottomButtonRow(Control row, Control container, Control content, int height)
		{
			const int margin = 6;
			row.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
			row.Height = height;
			row.Top = Math.Max(content.Top + margin, container.ClientSize.Height - margin - height);
			content.Height = Math.Max(40, row.Top - content.Top - margin);
		}

		private void ModernButton_Paint(object sender, PaintEventArgs e)
		{
			Button button = sender as Button;
			if (button == null || button.ClientSize.Width < 3 || button.ClientSize.Height < 3)
				return;

			bool hovered = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
			Color background = hovered ? ModernAccent : ModernSurface;
			Color foreground = ModernForeground;
			Rectangle interior = new Rectangle(1, 1, button.ClientSize.Width - 2, button.ClientSize.Height - 2);
			using (SolidBrush brush = new SolidBrush(background))
				e.Graphics.FillRectangle(brush, interior);

			TextRenderer.DrawText(
				e.Graphics,
				button.Text,
				button.Font,
				interior,
				foreground,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
				TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
		}

		private void RebuildFastCardIndexes()
		{
			m_dicQuickSearch.Clear();
			m_dicForgeCardsByName.Clear();
			m_dicForgeCardsByNameAndExpansion.Clear();

			if (m_gdWads == null || m_gdWads.Cards == null)
				return;

			foreach (CardInfo card in m_gdWads.Cards)
			{
				string name = normalize(card.GetLocalizedName("en-US"));
				string expansion = NormalizeExpansion(card.Expansion);
				string searchText = String.Join(" ", new[]
				{
					card.LocalizedName,
					card.GetLocalizedName("en-US"),
					card.Filename,
					card.LocalizedTypeLine,
					card.Expansion,
					card.Artist
				}.Where(value => !String.IsNullOrEmpty(value))).ToUpperInvariant();

				m_dicQuickSearch[card] = searchText;
				if (!String.IsNullOrEmpty(name) && !m_dicForgeCardsByName.ContainsKey(name))
					m_dicForgeCardsByName.Add(name, card);

				string expansionKey = MakeForgeExpansionKey(name, expansion);
				if (!String.IsNullOrEmpty(name) && !String.IsNullOrEmpty(expansion) && !m_dicForgeCardsByNameAndExpansion.ContainsKey(expansionKey))
					m_dicForgeCardsByNameAndExpansion.Add(expansionKey, card);
			}
		}

		private CardInfo FindForgeCard(string name, string expansion)
		{
			CardInfo card;
			string normalizedName = normalize(name);
			string normalizedExpansion = NormalizeExpansion(expansion);

			if (!String.IsNullOrEmpty(normalizedExpansion) &&
				m_dicForgeCardsByNameAndExpansion.TryGetValue(MakeForgeExpansionKey(normalizedName, normalizedExpansion), out card))
				return card;

			return m_dicForgeCardsByName.TryGetValue(normalizedName, out card) ? card : null;
		}

		private static string NormalizeExpansion(string expansion)
		{
			return String.IsNullOrEmpty(expansion) ? String.Empty : expansion.Trim().TrimStart('|').Trim().ToUpperInvariant();
		}

		private static string MakeForgeExpansionKey(string name, string expansion)
		{
			return name + "\u001f" + expansion;
		}

		private void ApplySavedCardFiltersAndSearch()
		{
			if (m_gdWads == null || m_gdWads.Cards == null || m_bsCards == null)
				return;

			IEnumerable<CardInfo> cards = m_gdWads.Cards;
			if (Settings.GetSetting("Filtering", true))
			{
				if (Settings.GetSetting("AdvancedFiltering", false) && m_cfsCardFilterAdvanced != null)
					cards = cards.Where(card => m_cfsCardFilterAdvanced.IsAllowed(card));
				else if (m_fltCardFilter != null)
					cards = cards.Where(card => m_fltCardFilter.CheckAgainstFilter(card));
			}

			m_lstBaseFilteredCards = new SortableBindingList<CardInfo>(cards);
			ApplyQuickCardSearch();
		}

		private void ApplyQuickCardSearch()
		{
			if (m_bsCards == null || m_lstBaseFilteredCards == null)
				return;

			string query = GetQuickSearchText();
			SortableBindingList<CardInfo> visibleCards;
			if (query.Length == 0)
				visibleCards = m_lstBaseFilteredCards;
			else
			{
				string[] searchTerms = query.ToUpperInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				visibleCards = new SortableBindingList<CardInfo>(m_lstBaseFilteredCards.Where(card =>
				{
					string indexed;
					return m_dicQuickSearch.TryGetValue(card, out indexed) && searchTerms.All(term => indexed.Contains(term));
				}));
			}

			m_bsCards.DataSource = visibleCards;
			dgvCards.RowCount = visibleCards.Count;
			sslblCardsInListNum.Text = visibleCards.Count.ToString();
			if (m_lstCardSort != null && m_lstCardSort.Count > 0 && visibleCards.Count > 0)
				Tools.SortFromList(dgvCards, m_bsCards, m_lstCardSort);
			dgvCards.Invalidate();
			UpdateQuickSearchStatus(visibleCards.Count, m_lstBaseFilteredCards.Count);
		}

		private string GetQuickSearchText()
		{
			return m_txtQuickSearch == null || m_bQuickSearchPlaceholder ? String.Empty : m_txtQuickSearch.Text.Trim();
		}

		private void SetQuickSearchPlaceholder()
		{
			if (m_txtQuickSearch == null || m_txtQuickSearch.Focused || m_txtQuickSearch.Text.Length > 0)
				return;

			m_bQuickSearchPlaceholder = true;
			m_txtQuickSearch.ForeColor = ModernForeground;
			m_txtQuickSearch.Text = GetModernText("Search cards...", "Поиск карт...");
		}

		private void QuickSearch_Enter(object sender, EventArgs e)
		{
			if (!m_bQuickSearchPlaceholder)
				return;

			m_bQuickSearchPlaceholder = false;
			m_txtQuickSearch.Text = String.Empty;
			m_txtQuickSearch.ForeColor = ModernForeground;
		}

		private void QuickSearch_Leave(object sender, EventArgs e)
		{
			SetQuickSearchPlaceholder();
		}

		private void QuickSearch_TextChanged(object sender, EventArgs e)
		{
			if (m_bQuickSearchPlaceholder || m_tmrQuickSearch == null)
				return;

			m_tmrQuickSearch.Stop();
			m_tmrQuickSearch.Start();
			if (m_btnClearQuickSearch != null)
				m_btnClearQuickSearch.Enabled = GetQuickSearchText().Length > 0;
		}

		private void ClearQuickSearch_Click(object sender, EventArgs e)
		{
			if (m_txtQuickSearch == null)
				return;

			if (m_bQuickSearchPlaceholder)
			{
				m_bQuickSearchPlaceholder = false;
				m_txtQuickSearch.Text = String.Empty;
			}
			else
				m_txtQuickSearch.Clear();
			m_txtQuickSearch.Focus();
		}

		private void QuickSearchTimer_Tick(object sender, EventArgs e)
		{
			m_tmrQuickSearch.Stop();
			ApplyQuickCardSearch();
		}

		private void QueueCardPreview(CardInfo card)
		{
			if (card == null || m_tmrPreviewHover == null || (card == m_ciCurrentViewingCard && picFrame.Image != null))
				return;

			m_ciPendingPreview = card;
			m_tmrPreviewHover.Stop();
			m_tmrPreviewHover.Start();
		}

		private void CancelQueuedCardPreview()
		{
			m_ciPendingPreview = null;
			if (m_tmrPreviewHover != null)
				m_tmrPreviewHover.Stop();
		}

		private void PreviewHoverTimer_Tick(object sender, EventArgs e)
		{
			m_tmrPreviewHover.Stop();
			if (m_bScrolling)
			{
				m_tmrPreviewHover.Start();
				return;
			}

			CardInfo card = m_ciPendingPreview;
			m_ciPendingPreview = null;
			if (card != null && !IsDisposed)
				ShowCardInfo(card);
		}

		private void DeckBuilder_ModernKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Control && e.KeyCode == Keys.F)
			{
				m_txtQuickSearch.Focus();
				m_txtQuickSearch.SelectAll();
				e.Handled = true;
				e.SuppressKeyPress = true;
			}
			else if (e.KeyCode == Keys.Escape && m_txtQuickSearch != null && !m_bQuickSearchPlaceholder && m_txtQuickSearch.Text.Length > 0)
			{
				m_txtQuickSearch.Text = String.Empty;
				dgvCards.Focus();
				e.Handled = true;
			}
			else if (e.KeyCode == Keys.Delete && RemoveSelectedDeckCards())
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
			}
		}

		private void ConfigureDragAndDrop(DataGridView grid)
		{
			grid.AllowDrop = true;
			grid.MouseDown += CardGrid_MouseDown;
			grid.MouseMove += CardGrid_MouseMove;
			grid.DragEnter += CardGrid_DragEnter;
			grid.DragOver += CardGrid_DragOver;
			grid.DragDrop += CardGrid_DragDrop;
			grid.DragLeave += CardGrid_DragLeave;
			grid.Paint += CardGrid_Paint;
		}

		private void CardGrid_MouseDown(object sender, MouseEventArgs e)
		{
			DataGridView grid = sender as DataGridView;
			if (grid == null || e.Button != MouseButtons.Left)
				return;

			DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
			if (hit.RowIndex < 0)
				return;

			if (!grid.Rows[hit.RowIndex].Selected && ModifierKeys == Keys.None)
			{
				grid.ClearSelection();
				grid.Rows[hit.RowIndex].Selected = true;
			}
			m_ptDragStart = e.Location;
			m_dgvDragSource = grid;
		}

		private void CardGrid_MouseMove(object sender, MouseEventArgs e)
		{
			DataGridView grid = sender as DataGridView;
			if (grid == null || grid != m_dgvDragSource || e.Button != MouseButtons.Left)
				return;

			Rectangle dragArea = new Rectangle(
				m_ptDragStart.X - SystemInformation.DragSize.Width / 2,
				m_ptDragStart.Y - SystemInformation.DragSize.Height / 2,
				SystemInformation.DragSize.Width,
				SystemInformation.DragSize.Height);
			if (dragArea.Contains(e.Location))
				return;

			CardDragPayload payload = GetDragPayload(grid);
			if (payload != null)
			{
				try
				{
					grid.DoDragDrop(payload, payload.Source == DeckLocation.MasterCards ? DragDropEffects.Copy : DragDropEffects.Move);
				}
				finally
				{
					ClearDropCue();
				}
			}
			m_dgvDragSource = null;
		}

		private CardDragPayload GetDragPayload(DataGridView grid)
		{
			if (grid.SelectedRows.Count == 0)
				return null;

			CardDragPayload payload = new CardDragPayload();
			payload.Source = GetDeckLocation(grid);
			IEnumerable<DataGridViewRow> selectedRows = grid.SelectedRows.Cast<DataGridViewRow>()
				.Where(row => !row.IsNewRow)
				.OrderBy(row => row.Index);
			foreach (DataGridViewRow row in selectedRows)
			{
				CardDragItem item = new CardDragItem();
				if (payload.Source == DeckLocation.MasterCards)
				{
					SortableBindingList<CardInfo> cards = m_bsCards == null ? null : m_bsCards.DataSource as SortableBindingList<CardInfo>;
					if (cards == null || row.Index >= cards.Count)
						continue;
					item.Card = cards[row.Index];
				}
				else
				{
					item.DeckCard = row.DataBoundItem as DeckCard;
					item.Card = item.DeckCard == null ? null : item.DeckCard.Card;
				}
				if (item.Card != null)
					payload.Items.Add(item);
			}
			return payload.Items.Count == 0 ? null : payload;
		}

		private void CardGrid_DragEnter(object sender, DragEventArgs e)
		{
			SetDragEffect(sender as DataGridView, e);
		}

		private void CardGrid_DragOver(object sender, DragEventArgs e)
		{
			SetDragEffect(sender as DataGridView, e);
		}

		private void CardGrid_DragLeave(object sender, EventArgs e)
		{
			if (sender == m_dgvDropTarget)
				ClearDropCue();
		}

		private void SetDragEffect(DataGridView targetGrid, DragEventArgs e)
		{
			CardDragPayload payload = e.Data.GetData(typeof(CardDragPayload)) as CardDragPayload;
			if (targetGrid == null || payload == null || payload.Items.Count == 0 || m_dkWorking == null)
			{
				e.Effect = DragDropEffects.None;
				ClearDropCue();
				return;
			}

			DeckLocation target = GetDeckLocation(targetGrid);
			int promoCardsToAdd = target == DeckLocation.PromoUnlocks && payload.Source != DeckLocation.PromoUnlocks ? payload.Items.Count : 0;
			if (target == DeckLocation.MasterCards || (target == DeckLocation.MainDeck && payload.Source == target) ||
				m_dkWorking.PromoUnlockCardCount + promoCardsToAdd > 10)
			{
				e.Effect = DragDropEffects.None;
				ClearDropCue();
			}
			else
			{
				e.Effect = payload.Source == DeckLocation.MasterCards ? DragDropEffects.Copy : DragDropEffects.Move;
				UpdateDropCue(targetGrid, e);
			}
		}

		private void CardGrid_DragDrop(object sender, DragEventArgs e)
		{
			DataGridView targetGrid = sender as DataGridView;
			CardDragPayload payload = e.Data.GetData(typeof(CardDragPayload)) as CardDragPayload;
			if (targetGrid == null || payload == null || e.Effect == DragDropEffects.None)
				return;

			DeckLocation target = GetDeckLocation(targetGrid);
			int targetIndex = GetDropTargetIndex(targetGrid, e.X, e.Y);
			bool changed = false;
			List<DeckCard> transferredCards = new List<DeckCard>();

			if (payload.Source == target && (target == DeckLocation.RegularUnlocks || target == DeckLocation.PromoUnlocks))
			{
				changed = ReorderUnlocks(payload, target, targetIndex);
				transferredCards.AddRange(payload.Items.Where(item => item.DeckCard != null).Select(item => item.DeckCard));
			}
			else if (payload.Source != target)
			{
				foreach (CardDragItem item in payload.Items)
				{
					DeckCard added = AddDraggedCard(item, target);
					if (added == null)
						continue;
					transferredCards.Add(added);
					RemoveDraggedCard(item, payload.Source);
					changed = true;
				}
			}

			ClearDropCue();
			if (changed)
			{
				m_dkWorking.Edited = true;
				RefreshDragDropState();
				SelectTransferredCards(targetGrid, transferredCards);
			}
		}

		private DeckCard AddDraggedCard(CardDragItem item, DeckLocation target)
		{
			int bias = item.DeckCard == null ? 1 : item.DeckCard.Bias;
			bool promo = item.DeckCard != null && item.DeckCard.Promo;

			if (target == DeckLocation.MainDeck)
				return m_dkWorking.AddCard(item.Card, bias, promo);
			else if (target == DeckLocation.RegularUnlocks)
			{
				DeckCard added = new DeckCard(item.Card, 1, bias, promo);
				m_dkWorking.RegularUnlocks.Cards.Add(added);
				return added;
			}
			else if (target == DeckLocation.PromoUnlocks && m_dkWorking.PromoUnlockCardCount < 10)
			{
				DeckCard added = new DeckCard(item.Card, 1, bias, promo);
				m_dkWorking.PromoUnlocks.Cards.Add(added);
				return added;
			}

			return null;
		}

		private void RemoveDraggedCard(CardDragItem item, DeckLocation source)
		{
			if (source == DeckLocation.MasterCards || item.DeckCard == null)
				return;

			if (item.DeckCard.Quantity > 1)
			{
				item.DeckCard.Quantity--;
				return;
			}

			if (source == DeckLocation.MainDeck)
				m_dkWorking.Cards.Remove(item.DeckCard);
			else if (source == DeckLocation.RegularUnlocks)
				m_dkWorking.RegularUnlocks.Cards.Remove(item.DeckCard);
			else if (source == DeckLocation.PromoUnlocks)
				m_dkWorking.PromoUnlocks.Cards.Remove(item.DeckCard);
		}

		private bool ReorderUnlocks(CardDragPayload payload, DeckLocation location, int targetIndex)
		{
			SortableBindingList<DeckCard> cards = location == DeckLocation.RegularUnlocks
				? m_dkWorking.RegularUnlocks.Cards
				: m_dkWorking.PromoUnlocks.Cards;
			List<DeckCard> original = cards.ToList();
			List<DeckCard> moving = payload.Items.Select(item => item.DeckCard)
				.Where(card => card != null && cards.Contains(card))
				.ToList();
			if (moving.Count == 0)
				return false;

			int removedBeforeTarget = moving.Count(card => cards.IndexOf(card) < targetIndex);
			foreach (DeckCard card in moving)
				cards.Remove(card);
			targetIndex -= removedBeforeTarget;
			targetIndex = Math.Max(0, Math.Min(targetIndex, cards.Count));
			foreach (DeckCard card in moving)
				cards.Insert(targetIndex++, card);

			return !original.SequenceEqual(cards);
		}

		private DeckLocation GetDeckLocation(DataGridView grid)
		{
			if (grid == dgvDeckCards)
				return DeckLocation.MainDeck;
			if (grid == dgvUnlocksRegular)
				return DeckLocation.RegularUnlocks;
			if (grid == dgvUnlocksPromo)
				return DeckLocation.PromoUnlocks;
			return DeckLocation.MasterCards;
		}

		private void UpdateDropCue(DataGridView targetGrid, DragEventArgs e)
		{
			int targetIndex = GetDropTargetIndex(targetGrid, e.X, e.Y);
			if (m_dgvDropTarget == targetGrid && m_nDropTargetIndex == targetIndex)
				return;

			DataGridView previousTarget = m_dgvDropTarget;
			m_dgvDropTarget = targetGrid;
			m_nDropTargetIndex = targetIndex;
			if (previousTarget != null && previousTarget != targetGrid)
				previousTarget.Invalidate();
			targetGrid.Invalidate();
		}

		private int GetDropTargetIndex(DataGridView grid, int screenX, int screenY)
		{
			Point local = grid.PointToClient(new Point(screenX, screenY));
			DataGridView.HitTestInfo hit = grid.HitTest(local.X, local.Y);
			if (hit.RowIndex < 0)
				return grid.Rows.Count;

			Rectangle rowBounds = grid.GetRowDisplayRectangle(hit.RowIndex, false);
			return local.Y > rowBounds.Top + rowBounds.Height / 2 ? hit.RowIndex + 1 : hit.RowIndex;
		}

		private void ClearDropCue()
		{
			DataGridView target = m_dgvDropTarget;
			m_dgvDropTarget = null;
			m_nDropTargetIndex = -1;
			if (target != null && !target.IsDisposed)
				target.Invalidate();
		}

		private void CardGrid_Paint(object sender, PaintEventArgs e)
		{
			DataGridView grid = sender as DataGridView;
			if (grid == null || grid != m_dgvDropTarget || m_nDropTargetIndex < 0)
				return;

			int y;
			if (grid.Rows.Count == 0)
				y = grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 1;
			else if (m_nDropTargetIndex >= grid.Rows.Count)
			{
				Rectangle lastRow = grid.GetRowDisplayRectangle(grid.Rows.Count - 1, false);
				y = lastRow.Height > 0 ? Math.Min(grid.ClientSize.Height - 2, lastRow.Bottom) : grid.ClientSize.Height - 2;
			}
			else
				y = grid.GetRowDisplayRectangle(m_nDropTargetIndex, false).Top;

			using (Pen pen = new Pen(ModernAccent, 3f))
				e.Graphics.DrawLine(pen, 1, y, Math.Max(1, grid.ClientSize.Width - 2), y);
		}

		private void SelectTransferredCards(DataGridView grid, IEnumerable<DeckCard> cards)
		{
			HashSet<DeckCard> selected = new HashSet<DeckCard>(cards.Where(card => card != null));
			if (selected.Count == 0)
				return;

			grid.ClearSelection();
			DataGridViewRow first = null;
			foreach (DataGridViewRow row in grid.Rows)
			{
				DeckCard card = row.DataBoundItem as DeckCard;
				if (card == null || !selected.Contains(card))
					continue;
				row.Selected = true;
				if (first == null)
					first = row;
			}
			if (first != null && first.Cells.Count > 0)
				grid.CurrentCell = first.Cells[0];
		}

		private bool RemoveSelectedDeckCards()
		{
			DataGridView grid = null;
			if (dgvDeckCards.Focused)
				grid = dgvDeckCards;
			else if (dgvUnlocksRegular.Focused)
				grid = dgvUnlocksRegular;
			else if (dgvUnlocksPromo.Focused)
				grid = dgvUnlocksPromo;

			if (grid == null || grid.IsCurrentCellInEditMode || grid.SelectedRows.Count == 0 || m_dkWorking == null)
				return false;

			DeckLocation source = GetDeckLocation(grid);
			List<DeckCard> cards = grid.SelectedRows.Cast<DataGridViewRow>()
				.Select(row => row.DataBoundItem as DeckCard)
				.Where(card => card != null)
				.Distinct()
				.ToList();
			if (cards.Count == 0)
				return false;

			foreach (DeckCard card in cards)
			{
				CardDragItem item = new CardDragItem();
				item.Card = card.Card;
				item.DeckCard = card;
				RemoveDraggedCard(item, source);
			}
			m_dkWorking.Edited = true;
			RefreshDragDropState();
			return true;
		}

		private void UpdateQuickSearchStatus(int visibleCount, int totalCount)
		{
			if (m_lblQuickSearchResults != null)
				m_lblQuickSearchResults.Text = visibleCount.ToString() + " / " + totalCount.ToString();
			if (m_btnClearQuickSearch != null)
				m_btnClearQuickSearch.Enabled = GetQuickSearchText().Length > 0;
		}

		private string GetModernText(string english, string russian)
		{
			return Settings.Language != null && Settings.Language.LanguageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
				? russian
				: english;
		}

		private void RefreshModernLocalizedStrings()
		{
			if (m_lblQuickSearch != null)
				m_lblQuickSearch.Text = GetModernText("Search:", "Поиск:");
			if (m_txtQuickSearch != null)
				m_txtQuickSearch.ToolTipText = GetModernText(
					"Search by name, filename, type, expansion or artist. Separate words may appear anywhere. (Ctrl+F)",
					"Поиск по названию, имени файла, типу, выпуску или художнику. Слова можно вводить в любом порядке. (Ctrl+F)");
			if (m_btnClearQuickSearch != null)
				m_btnClearQuickSearch.ToolTipText = GetModernText("Clear search (Esc)", "Очистить поиск (Esc)");

			if (m_txtQuickSearch != null && m_bQuickSearchPlaceholder)
			{
				m_txtQuickSearch.Text = GetModernText("Search cards...", "Поиск карт...");
				m_txtQuickSearch.ForeColor = ModernForeground;
			}
		}

		private void DeckBuilder_ModernFormClosed(object sender, FormClosedEventArgs e)
		{
			ClearDropCue();
			if (m_tmrQuickSearch != null)
			{
				m_tmrQuickSearch.Stop();
				m_tmrQuickSearch.Dispose();
				m_tmrQuickSearch = null;
			}
			if (m_tmrPreviewHover != null)
			{
				m_tmrPreviewHover.Stop();
				m_tmrPreviewHover.Dispose();
				m_tmrPreviewHover = null;
			}
			m_ciPendingPreview = null;
			m_dicQuickSearch.Clear();
			m_dicForgeCardsByName.Clear();
			m_dicForgeCardsByNameAndExpansion.Clear();
			m_lstBaseFilteredCards = null;
		}

		private void RefreshDragDropState()
		{
			lblBasicLandCount.Text = m_dkWorking.BasicLandAmount.ToString();
			lblTotalCardCount.Text = m_dkWorking.CardCount.ToString();
			lblRegUnlockCardCount.Text = m_dkWorking.RegularUnlockCardCount.ToString();
			lblPromoUnlockCardCount.Text = m_dkWorking.PromoUnlockCardCount.ToString();
			dgvDeckCards.Refresh();
			dgvUnlocksRegular.Refresh();
			dgvUnlocksPromo.Refresh();
			CheckRegularButtons();
			CheckPromoButtons();
			UpdateDGVLegality(dgvDeckCards, lblTotalCardCount);
			UpdateDGVLegality(dgvUnlocksRegular, lblRegUnlockCardCount);
			UpdateDGVLegality(dgvUnlocksPromo, lblPromoUnlockCardCount);
		}
	}
}

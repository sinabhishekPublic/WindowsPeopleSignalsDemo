using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;
using Windows.ApplicationModel.UserDataAccounts;
using Windows.Storage.Streams;

namespace PeopleSignalsPOC
{
    public sealed partial class MainWindow : Window
    {
        // =========================
        // Constants (one place, clear names)
        // =========================
        private const string PeopleContractAccountName = "com.microsoft.peoplecontract";
        private const string WindowsSystemPFN = "com.microsoft.windows.system";

        private const string ContactListName = "MyApp Contacts";
        private const string DemoRemoteId = "poc:user:ada"; // we reuse this RemoteId for the sample contact and its annotations

        // =========================
        // State
        // =========================
        private UserDataAccount? _uda;
        private ContactList? _contactList;
        private readonly ObservableCollection<AnnoView> _annoItems = new();

        public MainWindow()
        {
            InitializeComponent();
            AnnoList.ItemsSource = _annoItems;
            Log("App started.");
        }

        private void Log(string s) => LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {s}\r\n";

        // =========================
        // People account (UDA)
        // =========================
        private async Task<UserDataAccount> EnsurePeopleContractAccountAsync()
        {
            var store = await UserDataAccountManager.RequestStoreAsync(UserDataAccountStoreAccessType.AppAccountsReadWrite);

            var existing = (await store.FindAccountsAsync())
                .FirstOrDefault(a => a.UserDisplayName == PeopleContractAccountName);
            if (existing != null)
                return existing;

            var uda = await store.CreateAccountAsync(PeopleContractAccountName);
            try
            {
                // Allow Windows system experiences to read contacts/annotations from this account.
                uda.ExplictReadAccessPackageFamilyNames.Add(WindowsSystemPFN);
                await uda.SaveAsync();
            }
            catch
            {
                // Optional for sample: ignore if the property isn't available in a given SDK/sandbox.
            }
            return uda;
        }

        // =========================
        // Contact list
        // =========================
        private async Task<ContactList> EnsureContactListAsync()
        {
            _uda ??= await EnsurePeopleContractAccountAsync();

            var store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AppContactsReadWrite);
            var lists = await store.FindContactListsAsync();
            var existing = lists.FirstOrDefault(l => l.DisplayName == ContactListName);
            if (existing != null) return existing;

            var list = await store.CreateContactListAsync(ContactListName, _uda.Id);
            list.OtherAppReadAccess = ContactListOtherAppReadAccess.None; // 3P isolation; Windows system reads via UDA explicit access
            await list.SaveAsync();
            return list;
        }

        // =========================
        // Annotation list
        // =========================
        private async Task<ContactAnnotationList> EnsureAnnotationListAsync()
        {
            _uda ??= await EnsurePeopleContractAccountAsync();
            var annStore = await ContactManager.RequestAnnotationStoreAsync(ContactAnnotationStoreAccessType.AppAnnotationsReadWrite);
            var lists = await annStore.FindAnnotationListsAsync();
            return lists.FirstOrDefault() ?? await annStore.CreateAnnotationListAsync(_uda.Id);
        }

        // =========================
        // Contact helpers
        // =========================
        private static void ApplyDemoFields(Contact c)
        {
            c.FirstName = "Ada";
            c.LastName = "Lovelace (POC)";
            if (c.Emails.Count == 0) c.Emails.Add(new ContactEmail { Address = "ada@example.com" });
            if (c.Phones.Count == 0) c.Phones.Add(new ContactPhone { Number = "+1 555 0100" });
        }

        private async Task<Contact> CreateOrGetContactAsync()
        {
            _contactList ??= await EnsureContactListAsync();

            Contact? c = null;
            try
            {
                c = await _contactList.GetContactFromRemoteIdAsync(DemoRemoteId);
            }
            catch
            {
                var batch = await _contactList.GetContactReader().ReadBatchAsync();
                c = batch.Contacts.FirstOrDefault(x => x.RemoteId == DemoRemoteId);
            }

            if (c == null)
            {
                c = new Contact
                {
                    RemoteId = DemoRemoteId,
                    SourceDisplayPicture = RandomAccessStreamReference.CreateFromUri(
                        new Uri("ms-appx:///Assets/Square150x150Logo.png"))
                };
                ApplyDemoFields(c);
                await _contactList.SaveContactAsync(c);
                Log($"Contact created: Id={c.Id}, RemoteId={c.RemoteId}, Name={c.DisplayName}");
            }
            else
            {
                // Keep sample fields present (not editable via UI in this demo)
                ApplyDemoFields(c);
                await _contactList.SaveContactAsync(c);
                Log($"Contact saved: Id={c.Id}, RemoteId={c.RemoteId}, Name={c.DisplayName}");
            }

            return c;
        }

        // =========================
        // Activities / Annotations
        // =========================
        private async Task AddActivityAsync(Contact contact, string category, string action)
        {
            var annList = await EnsureAnnotationListAsync();

            var anno = new ContactAnnotation
            {
                // All activities for this contact use the SAME RemoteId => multiple rows under one key
                ContactId = contact.Id,
                RemoteId = contact.RemoteId,
                // Publicly safe flag; Activity (if available) would be used in newer contracts
                SupportedOperations = ContactAnnotationOperations.Share
            };

            anno.ProviderProperties["ActivityCategoryType"] = category; 
            anno.ProviderProperties["ActivityActionType"] = action;
            anno.ProviderProperties["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            var ok = await annList.TrySaveAnnotationAsync(anno);
            Log($"Saved activity annotation: {ok}");

            await RefreshAnnotationsAsync(contact);
        }

        private async Task RefreshAnnotationsAsync(Contact contact)
        {
            var annList = await EnsureAnnotationListAsync();
            var items = await annList.FindAnnotationsByRemoteIdAsync(contact.RemoteId);

            _annoItems.Clear();
            foreach (var a in items.Select(AnnoView.From))
                _annoItems.Add(a);

            Log($"Annotations for {contact.DisplayName} (count): {items.Count}");
        }

        private async Task ShowAllAnnotationsAsync(Contact contact)
        {
            var annList = await EnsureAnnotationListAsync();
            var items = await annList.FindAnnotationsByRemoteIdAsync(contact.RemoteId);

            Log($"----- {items.Count} annotation(s) for RemoteId='{contact.RemoteId}' -----");
            foreach (var a in items)
            {
                Log($"Id={a.Id}, ContactId={a.ContactId}, RemoteId={a.RemoteId}, Ops={a.SupportedOperations}");
                if (a.ProviderProperties != null && a.ProviderProperties.Count > 0)
                {
                    foreach (var kvp in a.ProviderProperties)
                        Log($"  {kvp.Key} = {kvp.Value}");
                }
                else
                {
                    Log("  <no provider properties>");
                }
            }
            Log("----- end -----");

            await RefreshAnnotationsAsync(contact);
        }

        private async Task DeleteAllAsync(Contact contact)
        {
            var annList = await EnsureAnnotationListAsync();
            var items = await annList.FindAnnotationsByRemoteIdAsync(contact.RemoteId);
            foreach (var a in items)
                await annList.DeleteAnnotationAsync(a);

            Log($"Deleted {items.Count} annotation(s) for RemoteId='{contact.RemoteId}'.");
            await RefreshAnnotationsAsync(contact);
        }

        private async Task DeleteContactAsync(Contact contact)
        {
            await DeleteAllAsync(contact); // hygiene
            _contactList ??= await EnsureContactListAsync();
            await _contactList.DeleteContactAsync(contact);
            _annoItems.Clear();
            Log($"Deleted contact {contact.DisplayName}");
        }

        // =========================
        // UI handlers
        // =========================
        private async void BtnCreateContact_Click(object sender, RoutedEventArgs e)
        {
            var c = await CreateOrGetContactAsync();
            await RefreshAnnotationsAsync(c);
        }

        private async void BtnAddActivity_Click(object sender, RoutedEventArgs e)
        {
            // Creating the contact for demo purposes if not existing
            var c = await CreateOrGetContactAsync();

            string category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "chat";
            string action = (ActionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "send";

            await AddActivityAsync(c, category, action);
        }

        private async void BtnShowAll_Click(object sender, RoutedEventArgs e)
        {
            // Creating the contact for demo purposes if not existing
            var c = await CreateOrGetContactAsync();
            await ShowAllAnnotationsAsync(c);
        }

        private async void BtnDeleteAll_Click(object sender, RoutedEventArgs e)
        {
            var c = await CreateOrGetContactAsync();
            await DeleteAllAsync(c);
        }

        private async void BtnDeleteContact_Click(object sender, RoutedEventArgs e)
        {
            var c = await CreateOrGetContactAsync();
            await DeleteContactAsync(c);
        }
    }

    // Row model for the ListView (kept minimal, strings only)
    public sealed class AnnoView
    {
        public string Id { get; init; } = "";
        public string RemoteId { get; init; } = "";
        public string Category { get; init; } = "";
        public string Action { get; init; } = "";
        public long Timestamp { get; init; }

        public string TimestampIso =>
            Timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(Timestamp).UtcDateTime.ToString("u")
                : "";

        public static AnnoView From(ContactAnnotation a)
        {
            string cat = a.ProviderProperties.TryGetValue("ActivityCategoryType", out var v1) ? v1?.ToString() ?? "" : "";
            string act = a.ProviderProperties.TryGetValue("ActivityActionType", out var v2) ? v2?.ToString() ?? "" : "";
            long ts = 0;
            if (a.ProviderProperties.TryGetValue("Timestamp", out var v3))
                long.TryParse(v3?.ToString(), out ts);

            return new AnnoView
            {
                Id = a.Id,
                RemoteId = a.RemoteId,
                Category = cat,
                Action = act,
                Timestamp = ts
            };
        }
    }
}

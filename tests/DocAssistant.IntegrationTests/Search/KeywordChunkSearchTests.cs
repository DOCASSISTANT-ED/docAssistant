using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Ingestion.Parsing;
using DocAssistant.Api.Modules.Search;
using DocAssistant.IntegrationTests.Infrastructure;
using DocAssistant.IntegrationTests.Ingestion;
using DocAssistant.IntegrationTests.TenantIsolation;
using DocAssistant.Shared.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.IntegrationTests.Search;

// Keyword search over chunks (docs/decisions.md #48, #49): PostgreSQL full-text search with
// the turkish configuration, any word of the question may match, best match first.
//
// The search runs as in the app (restricted database user, the scope's tenant, RLS). Each
// test writes its own chunks into a tenant created for it, so chunks of other tests never
// show up in its results.
[Collection(DatabaseCollection.Name)]
public class KeywordChunkSearchTests(PostgresFixture database) : IAsyncLifetime
{
    private TenantIsolationScenario _scenario = null!;
    private int _nextOrdinal;

    public async Task InitializeAsync()
    {
        await IngestionTestSupport.WaitForStartupRecoveryAsync(database);
        _scenario = await TenantIsolationScenario.CreateAsync(database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- What matches ----

    [Fact]
    public async Task ChunkContainingAWordOfTheQuestionIsFound()
    {
        var leave = await AddChunkAsync("Çalışanlar bir yılı doldurduklarında yıllık ücretli izne hak kazanır.");
        await AddChunkAsync("Depo alanında çelik burunlu ayakkabı zorunludur.");

        var results = await SearchAsync("ücretli");

        Assert.Equal([leave], results.Select(r => r.ChunkId));
    }

    // decisions #49: questions carry words such as "kaç" and "nedir" that documents do not.
    [Fact]
    public async Task OneMatchingWordIsEnough()
    {
        var allowance = await AddChunkAsync("Şehir dışı görevlendirmelerde günlük harcırah ödenir.");

        var results = await SearchAsync("Günlük harcırah kaç liradır, nedir?");

        Assert.Equal([allowance], results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task ChunkWithoutAnyWordOfTheQuestionIsNotFound()
    {
        await AddChunkAsync("Depo alanında çelik burunlu ayakkabı zorunludur.");

        Assert.Empty(await SearchAsync("harcırah"));
    }

    // Words are reduced to their stems on both sides, so a suffix does not hide a match.
    // PostgreSQL's Turkish stemmer does this well for some words and not for others
    // (decisions #49 lists it as a known limit); "belge" is one it handles.
    [Theory]
    [InlineData("belge")]
    [InlineData("belgelerin")]
    [InlineData("Belgeyi")]
    [InlineData("BELGELERDE")]
    public async Task WordIsFoundInAnotherInflection(string query)
    {
        var chunk = await AddChunkAsync("Belgeler arşivde on yıl saklanır.");

        var results = await SearchAsync(query);

        Assert.Equal([chunk], results.Select(r => r.ChunkId));
    }

    // decisions #49: the context header is part of the searched text.
    [Fact]
    public async Task ContextHeaderIsSearchedToo()
    {
        var chunk = await AddChunkAsync(
            "Belge: Araç Kullanım Talimatı > Bölüm 2: Sürüş Kuralları\n\nSürücü ve yolcular emniyet kemeri takar.");

        var results = await SearchAsync("talimatı");

        Assert.Equal([chunk], results.Select(r => r.ChunkId));
    }

    // What keyword search is for: exact terms the meaning-based search can miss.
    [Theory]
    [InlineData("1.500 TL hangi ödemenin tutarıdır?")]
    [InlineData("İnsan Kaynakları Md. kimdir?")]
    [InlineData("guvenlik@orneklojistik.example adresi ne içindir?")]
    public async Task ExactTermsAreFound(string query)
    {
        var chunk = await AddChunkAsync(
            "Günlük 1.500 TL harcırah ödenir. Sorumlusu İnsan Kaynakları Md. olup şüpheli e-postalar "
            + "guvenlik@orneklojistik.example adresine iletilir.");
        await AddChunkAsync("Depo alanında çelik burunlu ayakkabı zorunludur.");

        var results = await SearchAsync(query);

        Assert.Equal([chunk], results.Select(r => r.ChunkId));
    }

    // ---- Turkish casing (decisions #49) ----

    // I is the capital of ı, İ the capital of i. The database's own lowercasing would turn
    // "IŞIK" into "işik", which never matches "ışık".
    [Theory]
    [InlineData("IŞIKLANDIRMA kuralları", "ışıklandırma")]
    [InlineData("ışıklandırma kuralları", "IŞIKLANDIRMA")]
    [InlineData("ŞİKAYETLERİN ALINMASI", "şikayetlerin")]
    [InlineData("şikayetlerin alınması", "ŞİKAYETLERİN")]
    [InlineData("İZİN TALEPLERİ", "izin talepleri")]
    public async Task CapitalLettersMatchTheirTurkishSmallLetters(string content, string query)
    {
        var chunk = await AddChunkAsync(content);

        var results = await SearchAsync(query);

        Assert.Equal([chunk], results.Select(r => r.ChunkId));
    }

    // ---- Order ----

    [Fact]
    public async Task ChunkMatchingMoreWordsComesFirst()
    {
        var one = await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");
        var three = await AddChunkAsync("Konaklama giderleri ve harcırah fatura karşılığında ödenir.");
        var two = await AddChunkAsync("Konaklama ve harcırah için ayrı form doldurulur.");

        var results = await SearchAsync("konaklama harcırah fatura");

        Assert.Equal([three, two, one], results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task ScoresArePositiveAndFallWithThePosition()
    {
        await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");
        await AddChunkAsync("Konaklama giderleri ve harcırah fatura karşılığında ödenir.");

        var scores = (await SearchAsync("konaklama harcırah fatura")).Select(r => r.Score).ToList();

        Assert.Equal(2, scores.Count);
        Assert.All(scores, score => Assert.True(score > 0));
        Assert.True(scores[0] > scores[1]);
    }

    // Fusion works on positions, so equal scores must not come back in a random order.
    [Fact]
    public async Task ChunksWithTheSameScoreComeInAStableOrder()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir."));
        }

        var first = await SearchAsync("harcırah");
        var second = await SearchAsync("harcırah");

        Assert.Equal(ids.Order(), first.Select(r => r.ChunkId));
        Assert.Equal(first, second);
    }

    // ---- Limit ----

    [Fact]
    public async Task AtMostLimitChunksAreReturnedBestFirst()
    {
        await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");
        var best = await AddChunkAsync("Konaklama giderleri ve harcırah fatura karşılığında ödenir.");
        var second = await AddChunkAsync("Konaklama ve harcırah için ayrı form doldurulur.");

        var results = await SearchAsync("konaklama harcırah fatura", limit: 2);

        Assert.Equal([best, second], results.Select(r => r.ChunkId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task LimitBelowOneGivesNothing(int limit)
    {
        await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");

        Assert.Empty(await SearchAsync("harcırah", limit));
    }

    // ---- Questions without searchable words ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    [InlineData("...")]
    [InlineData("ve")]
    public async Task QuestionWithoutSearchableWordsGivesNothing(string query)
    {
        await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir ve kayda geçer.");

        Assert.Empty(await SearchAsync(query));
    }

    // The question goes to the database as a value: characters that mean something in SQL
    // or in a search expression are just text.
    [Theory]
    [InlineData("harcırah & !muhasebe")]
    [InlineData("harcırah | (talep")]
    [InlineData("harcırah:* <-> 'talep'")]
    [InlineData("harcırah'; DROP TABLE chunks; --")]
    [InlineData("harcırah \\ % _ \"")]
    public async Task SpecialCharactersInTheQuestionAreHarmless(string query)
    {
        var chunk = await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");

        var results = await SearchAsync(query);

        Assert.Equal([chunk], results.Select(r => r.ChunkId));
    }

    // ---- Tenants ----

    // The search is raw SQL, which the query filter does not cover (decisions #14).
    [Fact]
    public async Task OnlyTheCallersTenantIsSearched()
    {
        var mine = await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");
        var theirs = await AddChunkAsync(
            "Harcırah talepleri muhasebe birimine iletilir.", _scenario.TenantB, _scenario.DocumentB1);

        Assert.Equal([mine], (await SearchAsync("harcırah", tenantId: _scenario.TenantA)).Select(r => r.ChunkId));
        Assert.Equal([theirs], (await SearchAsync("harcırah", tenantId: _scenario.TenantB)).Select(r => r.ChunkId));
    }

    // Two layers (decisions #14): the search's own tenant condition must hold without
    // help from Row-Level Security. The superuser bypasses RLS, so here only that
    // condition separates the tenants.
    [Fact]
    public async Task TenantConditionOfTheSearchHoldsWithoutRowLevelSecurity()
    {
        var mine = await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");
        await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.", _scenario.TenantB, _scenario.DocumentB1);

        await using var db = database.CreateOwnerDbContext(_scenario.TenantA);
        var search = new KeywordChunkSearch(db, new FixedTenant(_scenario.TenantA));

        var results = await search.SearchAsync("harcırah", 30, CancellationToken.None);

        Assert.Equal([mine], results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task WithoutATenantNothingIsFound()
    {
        await AddChunkAsync("Harcırah talepleri muhasebe birimine iletilir.");

        Assert.Empty(await SearchAsync("harcırah", selectTenant: false));
    }

    // ---- Helpers ----

    // One chunk in tenant A's first document unless told otherwise, written as the
    // superuser: the tests are about searching, not about how chunks get there.
    private async Task<Guid> AddChunkAsync(string content, Guid? tenantId = null, Guid? documentId = null)
    {
        await using var db = database.CreateOwnerDbContext(tenantId ?? _scenario.TenantA);

        var chunk = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId ?? _scenario.DocumentA1,
            DocumentVersion = 1,
            Ordinal = _nextOrdinal++,
            Content = content,
            SourceType = DocumentSourceType.Docx,
        };

        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        return chunk.Id;
    }

    // One search in its own scope with the tenant selected, as a request would have it.
    private async Task<IReadOnlyList<RankedChunk>> SearchAsync(
        string query, int limit = 30, Guid? tenantId = null, bool selectTenant = true)
    {
        await using var scope = database.Api.Services.CreateAsyncScope();

        if (selectTenant)
        {
            scope.ServiceProvider.GetRequiredService<ITenantSelector>().Select(tenantId ?? _scenario.TenantA);
        }

        return await scope.ServiceProvider.GetRequiredService<IKeywordChunkSearch>()
            .SearchAsync(query, limit, CancellationToken.None);
    }

    private sealed class FixedTenant(Guid tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;
    }
}

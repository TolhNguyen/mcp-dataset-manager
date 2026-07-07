using System.Dynamic;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

/// <summary>
/// Verifies QueryOutcomeReader against fake responses shaped exactly like the real return values
/// of DuckDbQueryService.QueryAsync / ExternalQueryService.QueryAsync, for the branches
/// DashboardService.GetWidgetDataAsync (and the widget save-time trial execution) rely on:
/// "completed" (extract result), "failed" (surface error.code/message), and "summary"/other
/// non-completed statuses that carry no error object (synthesize a safe fallback instead of null).
///
/// Fakes are built with ExpandoObject rather than C# anonymous types: anonymous types are
/// compiler-emitted as assembly-internal, and QueryOutcomeReader.Extract's `dynamic` member access
/// runs inside the api assembly — a dynamic call site can only see an anonymous type's members
/// when the object's type was ALSO declared in that same assembly. Anonymous types built here (in
/// the test assembly) fail that accessibility check with a RuntimeBinderException even though the
/// real production objects (built inside DuckDbQueryService/ExternalQueryService, both in the api
/// assembly alongside QueryOutcomeReader) resolve fine. ExpandoObject implements
/// IDynamicMetaObjectProvider, so the DLR resolves its members via the dynamic-object protocol
/// instead of the reflection/accessibility path — sidestepping the cross-assembly restriction
/// while still exercising the exact same member-name logic in Extract.
/// </summary>
public class QueryOutcomeReaderTests
{
    private static dynamic NewResponse() => new ExpandoObject();

    [Fact]
    public void Completed_status_extracts_result_and_no_error()
    {
        dynamic compactTable = NewResponse();
        compactTable.format = "compact_table";
        compactTable.columns = Array.Empty<object>();
        compactTable.rows = Array.Empty<object>();
        compactTable.row_count = 0;

        dynamic response = NewResponse();
        response.success = true;
        response.status = "completed";
        response.result = compactTable;
        response.error = null;

        var outcome = QueryOutcomeReader.Extract((object)response);

        Assert.True(outcome.Completed);
        Assert.Same((object)compactTable, outcome.Result);
        Assert.Null(outcome.ErrorCode);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public void Failed_status_surfaces_error_code_and_message()
    {
        dynamic error = NewResponse();
        error.code = ErrorCodes.ColumnNotFound;
        error.message = "Column 'foo' not found.";
        error.details = null;
        error.retryable = true;

        dynamic response = NewResponse();
        response.success = false;
        response.status = "failed";
        response.result = null;
        response.error = error;

        var outcome = QueryOutcomeReader.Extract((object)response);

        Assert.False(outcome.Completed);
        Assert.Null(outcome.Result);
        Assert.Equal(ErrorCodes.ColumnNotFound, outcome.ErrorCode);
        Assert.Equal("Column 'foo' not found.", outcome.ErrorMessage);
    }

    [Fact]
    public void Summary_status_with_no_error_object_gets_safe_fallback_instead_of_null()
    {
        dynamic response = NewResponse();
        response.success = true;
        response.status = "summary";
        response.result = null;
        response.error = null;

        var outcome = QueryOutcomeReader.Extract((object)response);

        Assert.False(outcome.Completed);
        Assert.Null(outcome.Result);
        Assert.Equal(ErrorCodes.TokenBudgetConfirmationRequired, outcome.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ErrorMessage));
    }

    [Fact]
    public void External_query_shape_without_dataset_ids_array_is_also_handled()
    {
        // ExternalQueryService's single-dataset response shape (engine = provider name, no
        // dataset_ids array) — confirms the reader only depends on the shared members.
        dynamic error = NewResponse();
        error.code = ErrorCodes.ExternalQueryFailed;
        error.message = "connection refused";
        error.details = null;
        error.retryable = false;

        dynamic execution = NewResponse();
        execution.engine = "postgresql";
        execution.elapsed_ms = 12;

        dynamic response = NewResponse();
        response.success = false;
        response.dataset_id = Guid.NewGuid();
        response.status = "failed";
        response.result = null;
        response.execution = execution;
        response.error = error;

        var outcome = QueryOutcomeReader.Extract((object)response);

        Assert.False(outcome.Completed);
        Assert.Equal(ErrorCodes.ExternalQueryFailed, outcome.ErrorCode);
    }
}

using System.ComponentModel.DataAnnotations;

namespace SseApi.Models;

public record Message(long Id, string Text, DateTime CreatedAt);

public class CreateMessageRequest
{
    [Required]
    [StringLength(1000)]
    public string Text { get; set; } = "";
}
